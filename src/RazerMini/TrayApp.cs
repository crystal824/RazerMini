using System.Diagnostics;
using Microsoft.Win32;
using RazerMini.Core.Devices;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;
using RazerMini.Core.Monitoring;
using RazerMini.Core.Overlay;
using RazerMini.Core.Rendering;
using RazerMini.Core.Settings;
using RazerMini.Platform;
using RazerMini.Ui;

namespace RazerMini;

/// <summary>
/// 托盘主体：NotifyIcon + 自适应轮询 + 右键菜单，**支持多台设备同时显示**。
/// 线程模型：轮询在后台线程，HID 访问由 <see cref="_ioGate"/> 串行化，
/// 结果通过 <see cref="Dispatcher"/> 回到 UI 线程更新图标与菜单。
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RazerMini";
    private const int TooltipMaxLength = 63;   // NotifyIcon.Text 的历史上限

    /// <summary>支持的品牌 VID（唯一来源见 <see cref="DeviceCatalog.SupportedVendorIds"/>）。</summary>
    private static readonly ushort[] SupportedVendors = DeviceCatalog.SupportedVendorIds;

    private readonly FileLog _log;
    private readonly Dispatcher _dispatcher = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly object _ioGate = new();
    private readonly int _iconSize;

    private readonly ToolStripMenuItem _headerItem;
    private readonly ToolStripMenuItem _detailItem;
    private readonly ToolStripMenuItem _dpiInfoItem;
    private readonly ToolStripMenuItem _dpiSubmenu;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _overlayItem;
    private readonly ToolStripMenuItem _overlayInteractiveItem;
    private readonly List<(int Dpi, ToolStripMenuItem Item)> _dpiItems = new();
    private readonly List<ToolStripMenuItem> _deviceItems = new();

    /// <summary>桌面浮窗。创建失败时为 null，其余功能照常。</summary>
    private OverlayForm? _overlay;

    /// <summary>设备插拔通知（插上就重扫，不必等下一次轮询）。</summary>
    private DeviceNotifications? _deviceWatch;

    /// <summary>插拔通知会成串到达，这里做节流。</summary>
    private long _lastDeviceChangeMs;

    private const int DeviceChangeDebounceMs = 1500;

    /// <summary>
    /// XInput 哨兵：每 1.5 秒扫一次 4 个槽位（XInputGetBatteryInformation 是微秒级，代价可忽略）。
    /// Xbox 手柄走 XBOXGIP，开机/连线**不会**产生 HID 的 WM_DEVICECHANGE，
    /// 只在 60 秒一次的轮询里查的话，用户要等将近一分钟才看得到（真机日志 2026-11-07 证实）。
    /// </summary>
    private readonly System.Windows.Forms.Timer _padWatch = new() { Interval = 1500 };

    private string _padSignature = "-";

    /// <summary>被判定"这个接口不能用"的路径 → 冷却到什么时候再试（毫秒时间戳）。</summary>
    private readonly Dictionary<string, long> _rejectedUntil = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>接口明确不支持（0x05）/权限不够时的冷却时间。期间不再每轮去开它、刷一堆错误日志。</summary>
    private const int RejectedCooldownMs = 10 * 60 * 1000;

    private AppSettings _settings;
    private PollPolicy _policy;

    private readonly List<ConnectedDevice> _devices = new();
    private readonly Dictionary<string, BatterySample> _samples = new();
    private readonly Dictionary<string, int> _dpiByDevice = new();
    private readonly Dictionary<string, int> _deviceFailures = new();

    /// <summary>
    /// 上一轮没读到电量的设备（按 Key）。只用来在界面上标"休眠·上次"，所以只需要"有没有"，
    /// 不需要原因文本 —— 具体原因在日志和诊断报告里。
    /// </summary>
    private readonly HashSet<string> _unreadable = new();

    /// <summary>
    /// 多久读不到才算"陈旧"、值得在界面上标出来（默认 3 分钟 ≈ 3 轮轮询）。
    ///
    /// 为什么需要这个门槛：无线键鼠每 60 秒轮询一次，漏一轮是**常态**（高斯键盘命中率约 50%）。
    /// 只要漏一轮就显示"（休眠·上次）"，菜单就会一分钟闪一次括号，用户看不出任何有用信息
    /// （2026-10-27 用户直接问"为什么有个括号，过一会就没了"）。
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    /// 每轮轮询都重扫设备（枚举已知接口很便宜，已连上的会直接跳过）。
    /// 真正的"秒级发现"靠 <see cref="DeviceNotifications"/>；这里是兜底（蓝牙重连、接收器重枚举）。
    /// </summary>
    private const int RescanEveryPolls = 1;

    /// <summary>
    /// 单台设备连续读失败几次后剔除、交给下次重扫重连。
    /// ⚠️ **只对 <see cref="ReadOutcome.HandleDead"/> 计数**；
    /// 无线设备读不到电量（休眠/关机）是常态，不能因此把它从托盘里摘掉。
    /// </summary>
    private const int MaxDeviceFailures = 3;

    /// <summary>连续多少轮"设备都在但一台都读不到"之后，把轮询退到 <see cref="SleepyInterval"/>。</summary>
    private const int SleepyBackoffAfterFailures = 6;

    /// <summary>全部设备都在睡时的轮询间隔（5 分钟）。用户一动鼠标就会在下一轮恢复。</summary>
    private static readonly TimeSpan SleepyInterval = TimeSpan.FromMinutes(5);

    private int _pollCount;

    /// <summary>下一次轮询强制重扫设备（手动"立即刷新"时置位）。</summary>
    private bool _forceRescan;

    private string? _selectedKey;
    private string _deviceMenuItemSignature = string.Empty;
    private string _lastLoggedError = string.Empty;
    private string _lastIconKey = string.Empty;
    private bool _connected;
    private bool _pollInFlight;
    private bool _disposed;

    public TrayApp()
    {
        _log = new FileLog(AppPaths.LogFile);
        _log.Info("========== RazerMini 启动 ==========");
        _log.Info($"版本 {typeof(TrayApp).Assembly.GetName().Version}，日志：{AppPaths.LogFile}");

        _settings = AppSettings.Load(AppPaths.SettingsFile, _log);
        _log.Verbose = _settings.VerboseLogging;
        _policy = _settings.CreatePolicy();
        _iconSize = Math.Clamp(SystemInformation.SmallIconSize.Width, 8, 64);

        _menu = new ContextMenuStrip();

        _headerItem = new ToolStripMenuItem("正在连接设备…") { Enabled = false };
        _detailItem = new ToolStripMenuItem("电量：--") { Enabled = false };
        _dpiInfoItem = new ToolStripMenuItem("DPI：--") { Enabled = false };
        _dpiSubmenu = new ToolStripMenuItem("调整 DPI");
        _autostartItem = new ToolStripMenuItem("开机启动") { CheckOnClick = true, Checked = IsAutostartEnabled() };

        // 桌面浮窗：一个开关控制显隐，另一个开关控制"能不能用鼠标动它"。
        // 后者默认不勾 = 点击穿透 + 位置锁定（用户明确要求的默认行为）。
        _overlayItem = new ToolStripMenuItem("桌面浮窗") { CheckOnClick = true, Checked = _settings.OverlayEnabled };
        _overlayInteractiveItem = new ToolStripMenuItem("浮窗可交互（移动 / 缩放）")
        {
            CheckOnClick = true,
            Checked = _settings.OverlayEnabled && _settings.OverlayInteractive,
            Enabled = _settings.OverlayEnabled,
        };

        var refreshItem = new ToolStripMenuItem("立即刷新");
        var diagItem = new ToolStripMenuItem("诊断信息…");
        var openLogItem = new ToolStripMenuItem("打开日志文件");
        var exitItem = new ToolStripMenuItem("退出");

        refreshItem.Click += (_, _) => RequestImmediatePoll();
        diagItem.Click += (_, _) => ShowDiagnostics();
        openLogItem.Click += (_, _) => OpenLogFile();
        exitItem.Click += (_, _) => ExitApplication();
        _autostartItem.Click += (_, _) => ToggleAutostart();
        _overlayItem.Click += (_, _) => ToggleOverlay();
        _overlayInteractiveItem.Click += (_, _) => ToggleOverlayInteractive();

        _menu.Items.Add(_headerItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_detailItem);
        _menu.Items.Add(_dpiInfoItem);
        _menu.Items.Add(_dpiSubmenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_overlayItem);
        _menu.Items.Add(_overlayInteractiveItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(diagItem);
        _menu.Items.Add(openLogItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        RebuildDpiMenu();

        // Icon 必须先于 Visible 赋值：先 Visible 会让 Shell_NotifyIcon 加一个没有图标的空项，
        // 之后即使补上 Icon 也可能不显示（2026-10-26 真机踩过）。
        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Text = "RazerMini：正在连接…",
            Icon = IconFactory.Build(_settings.ParsedIconMode, _iconSize, TrayVisualState.Disconnected),
        };
        _notifyIcon.DoubleClick += (_, _) => RequestImmediatePoll();
        _notifyIcon.Visible = true;
        _log.Info($"托盘图标已创建（{_iconSize}px，mode={_settings.ParsedIconMode}），Visible=true");

        ShowBalloon("已启动，正在读取电量…（图标可能在 ^ 溢出区）", ToolTipIcon.Info);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        CreateOverlay();
        StartDeviceWatch();
        StartPadWatch();

        UpdateVisuals();
        RequestImmediatePoll();
    }

    // ────────────────────────── 轮询 ──────────────────────────

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_pollInFlight) return;
        StartPoll();
    }

    private void RequestImmediatePoll()
    {
        // 先把标志立起来：轮询正在进行时（HID 一轮可能要几秒），
        // 插拔事件不能被直接丢掉，否则要等下一个完整周期才看到新设备。
        _forceRescan = true;

        if (_pollInFlight)
        {
            if (_timer.Interval > 1500) _timer.Interval = 1500;   // 让挂起的重扫尽快执行
            return;
        }

        StartPoll();
    }

    private void StartPoll()
    {
        _pollInFlight = true;
        Task.Run(() =>
        {
            ProbeResult result;
            try
            {
                result = Probe();
            }
            catch (Exception ex)
            {
                result = ProbeResult.Failure($"轮询异常：{ex.GetType().Name} {ex.Message}");
                _log.Error($"轮询未捕获异常：{ex}");
            }

            _dispatcher.Post(() => ApplyProbe(result));
        });
    }

    private ProbeResult Probe()
    {
        lock (_ioGate)
        {
            _pollCount++;

            if (_devices.Count == 0)
            {
                if (!TryConnectNewDevices(out string connectError))
                {
                    return ProbeResult.Failure(connectError);
                }
            }
            else if (_forceRescan || _pollCount % RescanEveryPolls == 0)
            {
                // 启动时没连上的设备（休眠中 / 还没插）到这里才有机会补进来。
                // 之前只有 _devices.Count == 0 才枚举，所以只要启动时连上一只，其它永远进不来。
                _forceRescan = false;
                TryConnectNewDevices(out _);
            }

            var reads = new List<DeviceRead>();
            var failures = new List<string>();
            var dead = new List<ConnectedDevice>();

            foreach (ConnectedDevice entry in _devices.ToList())
            {
                if (!entry.Device.TryReadBattery(_settings.ParsedBatteryScale, out BatterySample sample,
                        out string error, out ReadOutcome outcome))
                {
                    failures.Add($"{entry.Candidate.ModelName}: {outcome.Describe()}（{error}）");
                    _unreadable.Add(entry.Key);

                    // 只有"句柄彻底死了/设备拔了"才计入剔除阈值。
                    // 无线设备休眠时读不到电量是**正常状态**（雷柏/雷蛇接收器在线、鼠标不应答），
                    // 以前这里一律计数 → 3 轮后设备从托盘消失，用户看不到这只鼠标的电量格子。
                    if (outcome == ReadOutcome.HandleDead && RecordDeviceFailure(entry.Key))
                    {
                        dead.Add(entry);
                    }

                    continue;
                }

                _deviceFailures.Remove(entry.Key);   // 读成功即清零
                _unreadable.Remove(entry.Key);

                int? dpi = null;
                if (entry.Device.SupportsDpi)
                {
                    // 每次轮询都重读：用户会按鼠标上的 DPI 键换档，缓存住就永远显示第一次读到的值
                    // （2026-10-27 用户反馈"切档后托盘仍显示 800"）。
                    if (entry.Device.TryReadDpi(out int x, out _, out string dpiError))
                    {
                        dpi = x;
                    }
                    else
                    {
                        // 读失败保留上次的值（ApplyProbe 只在有值时才覆盖），别把界面清成 "--"
                        _log.Debug($"读 DPI 失败（{entry.Candidate.ModelName}）：{dpiError}");
                    }
                }

                reads.Add(new DeviceRead(entry.Key, sample, dpi));
            }

            foreach (ConnectedDevice entry in dead)
            {
                RemoveDevice(entry, $"连续 {MaxDeviceFailures} 次读不到，剔除后等下次重扫重连");
            }

            if (reads.Count == 0)
            {
                return ProbeResult.Failure(
                    failures.Count > 0 ? string.Join("；", failures) : "所有设备都没读到电量",
                    failures);
            }

            return ProbeResult.Success(reads, failures);
        }
    }

    private void ApplyProbe(ProbeResult result)
    {
        _pollInFlight = false;
        if (_disposed) return;

        if (result.Reads.Count > 0)
        {
            if (_policy.OnSuccess())
            {
                _log.Info($"设备就绪：{string.Join("，", result.Reads.Select(r => $"{r.Key} {r.Sample.Percent}%"))}");
            }

            foreach (DeviceRead read in result.Reads)
            {
                _samples[read.Key] = read.Sample;
                if (read.Dpi is int dpi) _dpiByDevice[read.Key] = dpi;
            }

            if (_selectedKey is null || !_samples.ContainsKey(_selectedKey))
            {
                _selectedKey = result.Reads[0].Key;
            }

            _connected = true;
            _lastLoggedError = string.Empty;

            foreach (string failure in result.Failures)
            {
                _log.Warn($"部分设备读取失败：{failure}");
            }
        }
        else
        {
            // 设备还在枚举里、只是这一轮读不到（无线鼠标/键盘休眠、关机）—— 这是 Degraded，不是"设备消失"。
            bool deviceMissing = _devices.Count == 0;
            bool changed = _policy.OnFailure(deviceMissing);
            _connected = !deviceMissing;

            foreach (string failure in result.Failures)
            {
                _log.Debug($"本轮读不到：{failure}");
            }

            if (changed || !string.Equals(_lastLoggedError, result.Error, StringComparison.Ordinal))
            {
                _log.Warn(deviceMissing
                    ? $"读取失败：{result.Error}"
                    : $"设备暂时没有应答（保留上次读数，不摘设备）：{result.Error}");
                _lastLoggedError = result.Error;
            }

            // 只有"一台都没连上"才重开句柄。设备在、只是不应答时，重开句柄没有意义
            // （下一轮轮询自然会再试，且无线设备唤醒要靠用户动它）。
            if (_policy.ShouldReopen && deviceMissing)
            {
                DisposeAllDevices($"连续失败 {_policy.ConsecutiveFailures} 次，重扫设备");
                ShowBalloon($"读取失败，正在重新连接：{result.Error}", ToolTipIcon.Warning);
            }
        }

        UpdateVisuals();

        // 轮询间隔自适应：设备都在、只是全都睡着时，前几轮仍按 RetryInterval 快速重试
        // （用户可能刚动了下鼠标），连续几轮都读不到之后就退到 5 分钟 ——
        // 为"已知在睡"的设备每分钟做几秒无效 I/O 没有意义（2026-10-27 实测最坏一轮 4.5s）。
        TimeSpan delay = _policy.NextDelay;
        bool allSleepy = result.Reads.Count == 0 && _devices.Count > 0 &&
                         _policy.ConsecutiveFailures >= SleepyBackoffAfterFailures;

        // 有设备"一次都没读到过"时主动加密到 12 秒：
        // 高斯键盘命中率只有约 50%，按 60 秒一轮会让人以为"程序不主动读键盘"。
        // 已经读到过的设备读失败不必加速 —— 那走的是 30 秒重试策略，别把接收器一直打满。
        bool needsAttention = _devices.Any(d => !_samples.ContainsKey(d.Key));

        if (allSleepy)
        {
            delay = SleepyInterval;
        }
        else if (needsAttention)
        {
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds, 12));
        }

        _timer.Interval = (int)Math.Clamp(delay.TotalMilliseconds, 1000, 3600_000);
    }

    /// <summary>
    /// 枚举接口，把"还没连上、但现在真的能应答"的设备补进 <see cref="_devices"/>。
    ///
    /// 用途：启动时鼠标在休眠 / 还没插，之后才醒过来或插上 —— 也要能出现在托盘里。
    /// 已经在列表里的不会再开第二个句柄（重复的立刻释放）。
    /// </summary>
    private bool TryConnectNewDevices(out string error)
    {
        error = string.Empty;

        HidEnumeration enumeration = HidEnumerator.EnumerateForVendors(_log, SupportedVendors);
        List<HidDeviceInfo> interfaces = enumeration.Devices;

        // ① 先摘掉"接口已经从枚举里消失"的设备（拔了鼠标/拔了接收器）。
        //    这是真正的移除，与"设备在但读不到电量"（休眠）必须分开处理。
        //    ⚠️ XInput 手柄（xinput://slotN）不在 HID 枚举里，必须排除，否则每轮重扫都会把它们删掉。
        if (enumeration.PathCount > 0)
        {
            var present = new HashSet<string>(interfaces.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
            foreach (ConnectedDevice gone in _devices
                         .Where(d => !d.Path.StartsWith(XboxControllerDiscovery.PathPrefix, StringComparison.Ordinal))
                         .Where(d => !present.Contains(d.Path))
                         .ToList())
            {
                RemoveDevice(gone, "接口已从枚举中消失（设备/接收器被拔掉）");
            }
        }

        // ② 已连上的接口直接跳过：否则每次重扫都会对同一只设备再开一个句柄、再跑一遍握手/查询
        var known = new HashSet<string>(_devices.Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
        interfaces = interfaces.Where(i => !known.Contains(i.Path)).ToList();

        // ②b 明确不支持 / 权限不够的接口进冷却（10 分钟）：否则每轮都去开一次雷蛇充电底座
        //     这种"91 字节通道存在但固件不认电量命令"的接口，日志里每 60 秒刷一串 ERR。
        long now = Environment.TickCount64;
        interfaces = interfaces.Where(i => !IsRejected(i.Path, now)).ToList();

        if (interfaces.Count > 0)
        {
            List<ConnectedDevice> connected = DeviceConnector.ConnectAll(interfaces, _log, out IReadOnlyList<ConnectAttempt> attempts);
            AddDevices(connected);

            foreach (ConnectAttempt attempt in attempts)
            {
                // 打开成功、但既没读成功也没登记 = 这个接口对我们没用（不支持该命令 / 权限不足）
                if (attempt.Opened && !attempt.ReadOk && !attempt.Registered)
                {
                    _rejectedUntil[attempt.Candidate.Info.Path] = now + RejectedCooldownMs;
                    _log.Debug($"接口进入冷却：{attempt.ModelName}（{attempt.Detail}）");
                }
            }
        }

        // ③ XInput（Xbox 手柄）不在 HID 世界里，单独发现。每轮重扫都查一次：
        //    4 次 XInputGetBatteryInformation 是微秒级的，比开 HID 句柄便宜得多。
        //    槽位断开也要**立刻摘掉**：原来靠"读失败 3 轮"才剔除，手柄关机后要在列表里挂两分钟。
        List<ConnectedDevice> pads = XboxControllerDiscovery.Discover(_log);
        var presentPads = new HashSet<string>(pads.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
        foreach (ConnectedDevice gone in _devices
                     .Where(d => d.Path.StartsWith(XboxControllerDiscovery.PathPrefix, StringComparison.Ordinal))
                     .Where(d => !presentPads.Contains(d.Path))
                     .ToList())
        {
            RemoveDevice(gone, "XInput 槽位已断开（手柄关机 / 断连）");
        }

        AddDevices(pads);

        if (_devices.Count == 0)
        {
            error = enumeration.PathCount > 0
                ? "没连上任何受支持的设备（见诊断报告）"
                : "没找到任何受支持的接口（设备没插 / 没被系统识别）";
            return false;
        }

        return true;
    }

    /// <summary>把发现到的设备并进列表（已存在的直接释放重复句柄）。</summary>
    private void AddDevices(List<ConnectedDevice> entries)
    {
        foreach (ConnectedDevice entry in entries)
        {
            if (_devices.Any(d => d.Key == entry.Key))
            {
                try { entry.Device.Dispose(); } catch { /* 重复句柄，释放即可 */ }
                continue;
            }

            _devices.Add(entry);
            _deviceFailures.Remove(entry.Key);
            _log.Info($"设备接入：{entry.Candidate.FamilyName} {entry.Candidate.ModelName}");
        }
    }

    /// <summary>记一次读失败，返回是否已达到"剔除"阈值。</summary>
    private bool RecordDeviceFailure(string key)
    {
        int count = _deviceFailures.TryGetValue(key, out int existing) ? existing + 1 : 1;
        _deviceFailures[key] = count;
        return count >= MaxDeviceFailures;
    }

    /// <summary>把一台设备从列表里摘掉（句柄坏掉 / 拔掉了），下次重扫会重新连。</summary>
    private void RemoveDevice(ConnectedDevice entry, string reason)
    {
        _devices.Remove(entry);
        _deviceFailures.Remove(entry.Key);
        _unreadable.Remove(entry.Key);
        _samples.Remove(entry.Key);
        _dpiByDevice.Remove(entry.Key);
        if (_selectedKey == entry.Key) _selectedKey = null;

        try { entry.Device.Dispose(); } catch { /* 句柄已坏，忽略 */ }
        _log.Warn($"剔除设备 {entry.Candidate.ModelName}：{reason}");
    }

    private void DisposeAllDevices(string reason)
    {
        lock (_ioGate)
        {
            if (_devices.Count == 0) return;
            _log.Info($"关闭全部设备句柄：{reason}（{_devices.Count} 台）");
            foreach (ConnectedDevice entry in _devices)
            {
                try { entry.Device.Dispose(); } catch { /* 句柄已坏，忽略 */ }
            }
            _devices.Clear();
        }

        // 保留 _samples / _dpiByDevice：句柄重开后同一路径还是那只设备，
        // "上次 92%"比"--"有价值（无线设备醒过来之前不会立刻答）。
        _deviceFailures.Clear();
        _unreadable.Clear();
        _policy.Reset();
    }

    // ────────────────────────── 界面更新 ──────────────────────────

    private ConnectedDevice? SelectedDevice =>
        _selectedKey is null ? null : _devices.FirstOrDefault(d => d.Key == _selectedKey);

    private void UpdateVisuals()
    {
        BatterySample? selectedSample = _selectedKey is not null && _samples.TryGetValue(_selectedKey, out BatterySample s)
            ? s
            : null;

        var state = selectedSample is { } sel
            ? new TrayVisualState(true, sel.Percent, sel.Charging == true)
            : TrayVisualState.Disconnected;

        string iconKey = $"{_settings.ParsedIconMode}|{_iconSize}|{state.Connected}|{state.Percent}|{state.Charging}";
        if (iconKey != _lastIconKey)
        {
            Icon? old = _notifyIcon.Icon;
            _notifyIcon.Icon = IconFactory.Build(_settings.ParsedIconMode, _iconSize, state);
            old?.Dispose();
            _lastIconKey = iconKey;
        }

        RebuildDeviceItemsIfNeeded();

        UpdateDeviceItemTexts();

        ConnectedDevice? selected = SelectedDevice;
        int staleCount = _devices.Count(d => IsStale(d.Key));
        _headerItem.Text = _devices.Count switch
        {
            0 => "未检测到支持的设备",
            1 => DeviceRowText(_devices[0]),
            _ => $"已连接 {_devices.Count} 台设备" +
                 (staleCount > 0 ? $"（{staleCount} 台暂未应答）" : string.Empty) +
                 "（点一行切换图标）",
        };

        _detailItem.Text = selectedSample is { } sample
            ? $"电量：{sample.Describe()}{BatteryRawSuffix(sample)}{StaleSuffix(_selectedKey)}"
            : $"电量：--（{Shorten(_lastLoggedError)}）";

        int? dpi = _selectedKey is not null && _dpiByDevice.TryGetValue(_selectedKey, out int d) ? d : null;
        bool canDpi = selected?.Device.SupportsDpi == true;
        _dpiInfoItem.Text = dpi is int value ? $"DPI：{value}" : canDpi ? "DPI：--" : "DPI：该设备不支持";
        _dpiSubmenu.Enabled = _connected && canDpi;

        foreach ((int preset, ToolStripMenuItem item) in _dpiItems)
        {
            item.Checked = dpi is int current && Math.Abs(current - preset) <= 1;
        }

        _notifyIcon.Text = BuildTooltip();

        // 托盘菜单、图标、浮窗三者共用同一份状态，避免浮窗显示的电量和菜单不一致
        SyncOverlay();
    }

    /// <summary>设备列表变了才重建菜单项，避免菜单打开时反复重建导致闪烁/丢点击。</summary>
    private void RebuildDeviceItemsIfNeeded()
    {
        string signature = string.Join("|", _devices.Select(d => d.Key));
        if (signature == _deviceMenuItemSignature) return;
        _deviceMenuItemSignature = signature;

        foreach (ToolStripMenuItem item in _deviceItems)
        {
            _menu.Items.Remove(item);
            item.Dispose();
        }
        _deviceItems.Clear();

        int insertAt = 1;   // 紧跟在 header 之后
        foreach (ConnectedDevice entry in _devices)
        {
            // 建出来就带文本：别指望"刷新时再填"，那样一旦刷新路径没跑到，菜单里就是一行空白
            var item = new ToolStripMenuItem(DeviceRowText(entry));
            string key = entry.Key;
            item.Click += (_, _) => SelectDevice(key);
            _menu.Items.Insert(insertAt++, item);
            _deviceItems.Add(item);
        }
    }

    /// <summary>
    /// 给设备行填名字 / 电量 / 选中标记。
    ///
    /// 之前这三行是 `new ToolStripMenuItem()` 之后就没再赋 `Text` —— 菜单里出现三条空白，
    /// 点得动也能切，但用户根本看不出哪行是哪只鼠标（2026-10-26 用户截图反馈）。
    /// 文本每次刷新都重写，因为电量会变。
    /// </summary>
    private void UpdateDeviceItemTexts()
    {
        int count = Math.Min(_deviceItems.Count, _devices.Count);
        for (int i = 0; i < count; i++)
        {
            ConnectedDevice device = _devices[i];
            ToolStripMenuItem item = _deviceItems[i];
            item.Text = DeviceRowText(device);
            item.Checked = device.Key == _selectedKey;   // 勾选标出当前显示在图标上的那只
            item.ToolTipText = device.Path;              // 同型号两只（如 007B/007E）靠这个区分
        }
    }

    /// <summary>
    /// 这台设备的值算不算"陈旧"：这一轮没读到 **且**（从没读到过 或 上次成功已超过 <see cref="StaleAfter"/>）。
    /// 只看"这一轮失败"会造成括号每分钟闪一次，见 <see cref="StaleAfter"/> 的说明。
    /// </summary>
    private bool IsStale(string key) =>
        _unreadable.Contains(key) &&
        (!_samples.TryGetValue(key, out BatterySample sample) ||
         DateTimeOffset.Now - sample.Timestamp >= StaleAfter);

    /// <summary>设备行的文本：`雷蛇 Viper Ultimate（无线接收器） · 92%`；确实读不到时补状态。</summary>
    private string DeviceRowText(ConnectedDevice device)
    {
        string family = device.Candidate.FamilyName;
        string model = device.Candidate.ModelName;
        string name = model.Contains(family, StringComparison.OrdinalIgnoreCase)
            ? model
            : $"{family} {model}";

        bool hasSample = _samples.TryGetValue(device.Key, out BatterySample sample);
        string percent = hasSample ? $"{sample.Percent}%" : "--";

        // 读不到 ≠ 0%：无线设备休眠时接收器会回一个"无数据"应答。
        // 这里显示**上次的真实读数**并标明状态，绝不把 0 当成电量；
        // 偶尔漏一轮（键盘命中率约 50%）不标，免得菜单每分钟闪括号。
        string state = !IsStale(device.Key)
            ? string.Empty
            : hasSample ? "（休眠·上次）" : "（未应答）";

        return $"{name} · {percent}{state}";
    }

    /// <summary>详情行的后缀：确实读不到时补一句"多久没读到"。</summary>
    private string StaleSuffix(string? key)
    {
        if (key is null || !IsStale(key)) return string.Empty;
        if (!_samples.TryGetValue(key, out BatterySample sample)) return " · 未应答（还没读到过）";

        int minutes = Math.Max(1, (int)(DateTimeOffset.Now - sample.Timestamp).TotalMinutes);
        return $" · 未应答（上次读到是 {minutes} 分钟前）";
    }

    private void SelectDevice(string key)
    {
        _selectedKey = key;
        int? dpi = _dpiByDevice.TryGetValue(key, out int value) ? value : null;
        _log.Info($"切换图标显示设备：{key}（DPI={dpi?.ToString() ?? "未知"}）");
        UpdateVisuals();
    }

    private string BatteryRawSuffix(BatterySample sample) =>
        _settings.ShowRawBatteryInTooltip ? $"（原始 {sample.RawValue}，{sample.Scale}）" : string.Empty;

    private string BuildTooltip()
    {
        if (_devices.Count == 0)
        {
            return _lastLoggedError.Length > 0
                ? $"RazerMini：{Shorten(_lastLoggedError)}"
                : "RazerMini：未检测到设备";
        }

        // NotifyIcon.Text 只有 63 字符：3 台以上就列不全了（加了高斯键盘之后正好超），
        // 这时只报"当前图标显示的那只 + 共几台"，其余的点开菜单看。
        if (_devices.Count > 2 && _selectedKey is not null &&
            _samples.TryGetValue(_selectedKey, out BatterySample selectedSample))
        {
            string? model = _devices.FirstOrDefault(d => d.Key == _selectedKey)?.Candidate.ModelName;
            string stale = IsStale(_selectedKey) ? "（休眠）" : string.Empty;
            string tip = $"{model ?? "当前设备"} {selectedSample.Percent}%{stale}" +
                         $"（共 {_devices.Count} 台，右键切换）";
            return tip.Length <= TooltipMaxLength ? tip : tip.Substring(0, TooltipMaxLength);
        }

        var parts = new List<string>();
        foreach (ConnectedDevice entry in _devices)
        {
            if (!_samples.TryGetValue(entry.Key, out BatterySample sample)) continue;

            // `?` = 已经几分钟没读到，显示的是上次读数（无线休眠时很常见）
            string mark = IsStale(entry.Key) ? "?" : string.Empty;
            parts.Add($"{entry.Candidate.ModelName} {sample.Percent}%{mark}");
        }

        string text = parts.Count > 0
            ? string.Join(" · ", parts)
            : $"RazerMini：{_devices.Count} 台设备暂未应答（休眠中）";
        return text.Length <= TooltipMaxLength ? text : text.Substring(0, TooltipMaxLength);
    }

    private static string Shorten(string text) =>
        string.IsNullOrEmpty(text) ? "无原因" : text.Length <= 30 ? text : text.Substring(0, 30) + "…";

    private void RebuildDpiMenu()
    {
        _dpiItems.Clear();
        _dpiSubmenu.DropDownItems.Clear();

        foreach (int preset in _settings.DpiPresets.Distinct().OrderBy(p => p))
        {
            var item = new ToolStripMenuItem($"{preset} DPI");
            item.Click += (_, _) => ApplyDpi(preset);
            _dpiSubmenu.DropDownItems.Add(item);
            _dpiItems.Add((preset, item));
        }

        _dpiSubmenu.DropDownItems.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("自定义…");
        custom.Click += (_, _) => PromptCustomDpi();
        _dpiSubmenu.DropDownItems.Add(custom);
    }

    // ────────────────────────── DPI ──────────────────────────

    private void PromptCustomDpi()
    {
        int max = SelectedDevice?.Device.MaxDpi is > 0 and int m ? m : 20000;
        int initial = _selectedKey is not null && _dpiByDevice.TryGetValue(_selectedKey, out int dpi) ? dpi : 1600;

        using var dialog = new DpiDialog(max, initial);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            ApplyDpi(dialog.SelectedDpi);
        }
    }

    private void ApplyDpi(int dpi)
    {
        ConnectedDevice? entry = SelectedDevice;
        if (entry is null || !entry.Device.SupportsDpi)
        {
            ShowBalloon("当前选中的设备不支持改 DPI", ToolTipIcon.Warning);
            _log.Warn($"尝试设置 DPI {dpi} 时选中的设备不支持");
            return;
        }

        int max = entry.Device.MaxDpi > 0 ? entry.Device.MaxDpi : 20000;
        int target = Math.Clamp(dpi, 100, max);
        string key = entry.Key;
        _log.Info($"请求设置 DPI：{entry.Candidate.ModelName} {dpi} → 生效值 {target}");

        Task.Run(() =>
        {
            int? readBack = null;
            bool ok = false;
            string error = "设备已断开";

            lock (_ioGate)
            {
                if (_devices.Any(d => d.Key == key))
                {
                    ok = entry.Device.TryWriteDpi(target, target, out error);
                    if (ok)
                    {
                        Thread.Sleep(30);   // 让设备把新值落进固件再读回
                        if (entry.Device.TryReadDpi(out int x, out _, out _)) readBack = x;
                    }
                }
            }

            _dispatcher.Post(() =>
            {
                if (_disposed) return;
                if (ok)
                {
                    _dpiByDevice[key] = readBack ?? target;
                    _log.Info($"DPI 设置成功，读回 {readBack?.ToString() ?? "(未读回)"}");
                    ShowBalloon($"DPI 已设为 {_dpiByDevice[key]}", ToolTipIcon.Info);
                }
                else
                {
                    _log.Error($"DPI 设置失败：{error}");
                    ShowBalloon($"DPI 设置失败：{error}", ToolTipIcon.Warning);
                }
                UpdateVisuals();
            });
        });
    }

    // ────────────────────────── 设备插拔通知 ──────────────────────────

    /// <summary>订阅 WM_DEVICECHANGE：插上新设备后 1 秒内出现在浮窗/托盘里。</summary>
    private void StartDeviceWatch()
    {
        try
        {
            _deviceWatch = new DeviceNotifications();
            _deviceWatch.DevicesChanged += (_, _) => OnDevicesChanged();
            _log.Info("已订阅设备插拔通知（插上即重扫）");
        }
        catch (Exception ex)
        {
            _deviceWatch = null;
            _log.Warn($"订阅设备插拔通知失败（仍会按轮询重扫）：{ex.GetType().Name} {ex.Message}");
        }
    }

    private void OnDevicesChanged()
    {
        long now = Environment.TickCount64;
        if (now - _lastDeviceChangeMs < DeviceChangeDebounceMs) return;
        _lastDeviceChangeMs = now;

        // 插拔后清空拒绝缓存：重新插上的设备要立刻有机会被认领
        _rejectedUntil.Clear();

        _log.Info("检测到设备插拔，立即重扫");
        RequestImmediatePoll();
    }

    /// <summary>XInput 哨兵：槽位集合一变就立刻重扫，不等下一轮轮询。</summary>
    private void StartPadWatch()
    {
        _padWatch.Tick += OnPadWatchTick;
        _padWatch.Start();
    }

    private void OnPadWatchTick(object? sender, EventArgs e)
    {
        if (_disposed) return;

        List<XboxControllerDiscovery.XboxSlotState> slots = XboxControllerDiscovery.Scan();
        string signature = string.Join(",",
            slots.Where(s => s.HasBattery).Select(s => s.Slot).OrderBy(s => s));

        if (signature == _padSignature) return;
        _padSignature = signature;

        _log.Info($"XInput 槽位变化：[{(signature.Length == 0 ? "无手柄" : signature)}]，立即刷新设备列表");
        RequestImmediatePoll();
    }

    private bool IsRejected(string path, long now) =>
        _rejectedUntil.TryGetValue(path, out long until) && now < until;

    // ────────────────────────── 桌面浮窗 ──────────────────────────

    /// <summary>
    /// 创建浮窗。任何异常都只让"浮窗"这一项失效，托盘本体继续工作 ——
    /// 一个纯展示功能不该把电量读取带走。
    /// </summary>
    private void CreateOverlay()
    {
        try
        {
            _overlay = new OverlayForm(_settings, _log);
            _overlay.PlacementChanged += (_, _) => _log.Debug("浮窗位置/大小已落盘");

            SyncOverlay();

            if (_settings.OverlayEnabled)
            {
                _overlay.ShowOverlay(true);
                _overlay.SetInteractive(_settings.OverlayInteractive);
            }

            _overlayInteractiveItem.Enabled = _settings.OverlayEnabled;
            _log.Debug($"桌面浮窗就绪：{_overlay.DescribeState()}");
        }
        catch (Exception ex)
        {
            _overlay = null;
            _overlayItem.Checked = false;
            _overlayInteractiveItem.Enabled = false;
            _log.Error($"桌面浮窗创建失败（其余功能不受影响）：{ex}");
            ShowBalloon($"桌面浮窗创建失败：{ex.Message}", ToolTipIcon.Warning);
        }
    }

    private void SyncOverlay()
    {
        if (_overlay is null) return;

        try
        {
            _overlay.Apply(BuildSnapshot());
        }
        catch (Exception ex)
        {
            _log.Warn($"刷新浮窗失败：{ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 把当前设备状态拍成快照交给浮窗。**读不到电量的设备也照样出现**（显示 -- / NO RESPONSE），
    /// 因为"设备在但没应答"和"设备不在"是两件事，浮窗上必须能分辨。
    /// </summary>
    private OverlaySnapshot BuildSnapshot()
    {
        var devices = new List<OverlayDevice>(_devices.Count);

        foreach (ConnectedDevice entry in _devices)
        {
            bool hasSample = _samples.TryGetValue(entry.Key, out BatterySample sample);

            devices.Add(new OverlayDevice(
                entry.Key,
                entry.Candidate.FamilyName,
                entry.Candidate.ModelName,
                hasSample ? sample.Percent : null,
                hasSample && sample.Charging == true,
                IsStale(entry.Key),
                hasSample ? OverlayText.StageLabel(sample.StageLevel, sample.StageCount) : null));
        }

        return new OverlaySnapshot(devices, DateTimeOffset.Now);
    }

    private void ToggleOverlay()
    {
        bool enabled = _overlayItem.Checked;

        _settings.OverlayEnabled = enabled;
        _settings.Save(AppPaths.SettingsFile, _log);
        _overlayInteractiveItem.Enabled = enabled;

        if (_overlay is null)
        {
            if (enabled) CreateOverlay();
            return;
        }

        _overlay.ShowOverlay(enabled);

        if (!enabled)
        {
            // 隐藏时顺手把交互关掉：下次打开必须回到"不挡鼠标"的默认状态
            _overlayInteractiveItem.Checked = false;
            _settings.OverlayInteractive = false;
            _overlay.SetInteractive(false);
            _settings.Save(AppPaths.SettingsFile, _log);
        }

        _log.Info($"桌面浮窗：{(enabled ? "显示" : "隐藏")}");
    }

    private void ToggleOverlayInteractive()
    {
        if (_overlay is null)
        {
            _overlayInteractiveItem.Checked = false;
            return;
        }

        bool interactive = _overlayInteractiveItem.Checked;

        _settings.OverlayInteractive = interactive;
        _settings.Save(AppPaths.SettingsFile, _log);
        _overlay.SetInteractive(interactive);

        ShowBalloon(interactive
                ? "浮窗可交互：拖动空白处移动，拖四边/四角缩放；再点一次菜单项即恢复点击穿透"
                : "浮窗已锁定：鼠标完全穿透，位置固定",
            ToolTipIcon.Info);
    }

    // ────────────────────────── 杂项 ──────────────────────────

    private void ShowBalloon(string text, ToolTipIcon icon)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4000, "RazerMini", text, icon);
        }
        catch (Exception ex)
        {
            _log.Warn($"气泡提示失败：{ex.Message}");
        }
    }

    private void ShowDiagnostics()
    {
        string report = Diagnostics.BuildReport(_log, _settings) +
                        Environment.NewLine + Environment.NewLine +
                        "── 桌面浮窗 ──" + Environment.NewLine +
                        (_overlay?.DescribeState() ?? "(浮窗未创建)");
        _log.Info("生成了诊断报告");
        using var form = new DiagnosticsForm(report, AppPaths.LogFile);
        form.ShowDialog();
    }

    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"打开日志失败：{ex.Message}");
            ShowBalloon($"打开日志失败：{ex.Message}", ToolTipIcon.Warning);
        }
    }

    private static bool IsAutostartEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleAutostart()
    {
        bool enable = _autostartItem.Checked;
        try
        {
            string command = BuildSelfLaunchCommand();
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath)!;
            if (enable)
            {
                key.SetValue(RunValueName, command);
                _log.Info($"已开启开机启动：{command}");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                _log.Info("已关闭开机启动");
            }
            _settings.StartWithWindows = enable;
            _settings.Save(AppPaths.SettingsFile, _log);
        }
        catch (Exception ex)
        {
            _log.Error($"设置开机启动失败：{ex.Message}");
            _autostartItem.Checked = !enable;   // 回滚勾选状态
            ShowBalloon($"设置开机启动失败：{ex.Message}", ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// 拼自启命令。经 dotnet 宿主启动时 Environment.ProcessPath 是 dotnet.exe，
    /// 只写它会把自启项变成"启动一个空的 dotnet"，必须把 dll 路径一起写进去。
    /// </summary>
    private static string BuildSelfLaunchCommand()
    {
        string processPath = Environment.ProcessPath ?? string.Empty;
        string? entryAssembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        bool launchedByDotnetHost = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (launchedByDotnetHost && !string.IsNullOrEmpty(entryAssembly))
        {
            return $"\"{processPath}\" \"{entryAssembly}\"";
        }

        return $"\"{processPath}\"";
    }

    private void ExitApplication()
    {
        _log.Info("用户选择了退出");
        DisposeResources();
        ExitThread();
    }

    private void DisposeResources()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Dispose();

        _padWatch.Stop();
        _padWatch.Dispose();

        if (_deviceWatch is not null)
        {
            try { _deviceWatch.Dispose(); } catch { /* 退出阶段忽略 */ }
            _deviceWatch = null;
        }

        if (_overlay is not null)
        {
            try
            {
                _overlay.SavePlacement();
                _overlay.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn($"释放浮窗失败：{ex.Message}");
            }

            _overlay = null;
        }

        lock (_ioGate)
        {
            foreach (ConnectedDevice entry in _devices)
            {
                try { entry.Device.Dispose(); } catch { /* ignore */ }
            }
            _devices.Clear();
        }

        _notifyIcon.Visible = false;
        Icon? icon = _notifyIcon.Icon;
        _notifyIcon.Icon = null;
        icon?.Dispose();
        _notifyIcon.Dispose();
        _menu.Dispose();
        _dispatcher.DestroyHandle();
        _log.Info("========== RazerMini 退出 ==========");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeResources();
        base.Dispose(disposing);
    }

    private sealed record DeviceRead(string Key, BatterySample Sample, int? Dpi);

    private sealed record ProbeResult(List<DeviceRead> Reads, List<string> Failures, string Error)
    {
        public static ProbeResult Success(List<DeviceRead> reads, List<string> failures) =>
            new(reads, failures, string.Empty);

        public static ProbeResult Failure(string error, List<string>? failures = null) =>
            new(new List<DeviceRead>(), failures ?? new List<string>(), error);
    }
}
