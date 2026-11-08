using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

/// <summary>
/// 任天堂 Switch Pro 手柄（`057E:2009`，**蓝牙**连接）。只读电量，不支持 DPI。
///
/// 协议来源：Linux 内核 `drivers/hid/hid-nintendo.c`（joycon 驱动，权威实现）+ Chromium
/// `device/gamepad/nintendo_controller.cc`（Windows 上同样走 HID 收发，已验证可行性）。
///
/// ## 关键事实
///
/// **输入报告**（蓝牙）：
/// <code>
///   0x3F  简化报告（Windows 默认推这个）—— **没有电量字段**
///   0x30  标准全量报告（60Hz）—— 有电量
///   0x21  子命令应答 —— 电池字节不在下标 2（那里是 ack），不能按 0x30 解析
/// </code>
/// 0x30/0x31/0x32/0x33（full / NFC / IR 变体）共用同一个头：
/// <code>
///   [0]=报告 ID  [1]=timer  [2]=电池与连接信息  [3..5]=按键  [6..8]=左摇杆 …
/// </code>
///
/// **电量字节 `[2]` 的位域**（hid-nintendo `joycon_parse_battery_status`）：
/// <code>
///   bit0     = USB 供电（host powered）
///   bit4     = 正在充电
///   bit5..7  = 电量档位 0 空 / 1 低 / 2 中 / 3 高 / 4 满
/// </code>
/// ⚠️ 流传很广的"高 4 位就是电量"是错的 —— bit4 是充电位，会把"满电充电中"读成 8（非法档位）。
///
/// **输出报告 `0x01`（蓝牙专用，rumble + 子命令）**：
/// <code>
///   [0]=0x01  [1]=报文序号(0..15)  [2..9]=8 字节震动数据  [10]=子命令  [11..]=子命令数据
/// </code>
/// 有线走 `0x80`（`[0]=0x80 [1]=子命令`，没有震动段），本类默认按蓝牙处理（见 <see cref="IsBluetoothPath"/>）。
///
/// ## 为什么要发子命令
/// Windows 默认只给 `0x3F`（无电量）。必须发**子命令 `0x03` 数据 `0x30`**
/// （set input report mode = standard full）让设备切到 0x30 模式，之后每 1/60 秒就会推全量报告。
/// 设备休眠/重连后模式会复位，所以"读不到 0x30"时会再发一次（不是每轮无条件发）。
/// </summary>
public sealed class NintendoSwitchPro : IBatteryDevice
{
    public const ushort VendorId = 0x057E;
    public const ushort ProductId = 0x2009;
    public const ushort GamepadUsagePage = 0x0001;
    public const ushort GamepadUsage = 0x0005;

    // ── 输入报告 ──
    public const byte ReportInputFull = 0x30;
    public const byte ReportInputSimpleHid = 0x3F;
    public const byte ReportInputSubcmdReply = 0x21;
    public const byte ReportInputButtonEvent = 0x3E;

    /// <summary>0x30 报告的实际长度（含报告 ID）。HID caps 里的 InputReportLength 在蓝牙下可能报 362，按实际报文判断。</summary>
    public const int ReportLength = 49;

    /// <summary>电量字节在 0x30 报告里的下标。</summary>
    public const int BatteryByteIndex = 2;

    // ── 输出报告 ──
    public const byte OutputRumbleAndSubcmd = 0x01;
    public const byte OutputUsbCommand = 0x80;

    // ── 子命令 ──
    public const byte SubcmdSetInputReportMode = 0x03;
    public const byte SubcmdEnableImu = 0x40;
    public const byte SubcmdEnableVibration = 0x48;

    /// <summary>目标输入报告模式（standard full 0x30）。</summary>
    public const byte InputReportModeFull = 0x30;

    /// <summary>
    /// 中性震动数据（hid-nintendo 里的空震动帧）。子命令帧必须带 8 字节震动段 ——
    /// 少这段设备会当成非法帧静默丢弃。
    /// </summary>
    public static readonly byte[] RumbleNeutral = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };

    /// <summary>
    /// 5 档电量 → 百分比。XInput/Switch 这类手柄只有档位没有精确读数，这里按"格数"折算：
    /// 0 空 → 5%、1 低 → 25%、2 中 → 50%、3 高 → 75%、4 满 → 100%。
    /// </summary>
    public static readonly int[] LevelPercents = { 5, 25, 50, 75, 100 };

    public const string ModelName = "Switch Pro 手柄";

    private const int AwaitFullReportInitiallyMs = 300;
    private const int AwaitFullReportAfterSubcmdMs = 1500;
    private const int PollGapMs = 2;
    private const int MaxReportsPerWait = 240;

    private readonly IHidTransport _transport;
    private readonly IInterruptReader? _interruptReader;
    private readonly ILog _log;
    private byte _packetNumber;
    private bool _disposed;

    /// <summary>最近一次收到的报告 ID（诊断用）。</summary>
    public byte LastReportId { get; private set; }

    /// <summary>最近一次收到的报告原文（诊断用，前 16 字节）。</summary>
    public string LastReportHex { get; private set; } = string.Empty;

    /// <summary>最近一次解析出的档位 0..4（诊断用）。</summary>
    public int? LastLevel { get; private set; }

    /// <summary>是否已经发过"切 0x30 模式"的子命令（诊断用）。</summary>
    public bool ModeRequested { get; private set; }

    public NintendoSwitchPro(IHidTransport transport, ILog? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _interruptReader = transport as IInterruptReader;
        _log = log ?? NullLog.Instance;
        _log.Debug($"打开 Switch Pro 手柄: {ModelName}，通道 {transport.Info.Path}");
        _log.Debug($"  通道参数: usage={transport.Info.UsagePage:X4}/{transport.Info.Usage:X4} " +
                  $"in={transport.Info.InputReportLength} out={transport.Info.OutputReportLength} " +
                  $"可写={transport.CanWrite} 读方式={ReadMethod} 连接={LinkKind}");
        if (!IsBluetoothPath(transport.Info.Path))
        {
            _log.Warn("该手柄不是蓝牙通道（路径里没有 bth/bthenum），有线模式的 0x80 输出帧本版未验证");
        }
    }

    public string DisplayName => ModelName;
    public string DeviceId => $"{VendorId:X4}:{ProductId:X4}";

    /// <summary>手柄没有 DPI 概念。</summary>
    public int MaxDpi => 0;

    public bool SupportsDpi => false;
    public bool SupportsBattery => true;

    /// <summary>
    /// 不按句柄权限判断：写 output report 有 <c>HidD_SetOutputReport</c> 兜底（FILE_ANY_ACCESS），
    /// 零权限句柄也能发。真发不出去时 <see cref="TryReadBattery"/> 会返回 <see cref="ReadOutcome.NotWritable"/>。
    /// </summary>
    public bool CanWrite => true;

    public string ReadMethod =>
        _interruptReader is null ? "HidD_GetInputReport（取最近一条）" : "interrupt ReadFile（overlapped）";

    public string LinkKind => IsBluetoothPath(_transport.Info.Path) ? "蓝牙" : "USB/未知";

    /// <summary>
    /// 蓝牙通道的接口路径有三种写法，都要认：
    /// `hid#bthenum#…`（部分蓝牙栈）、`hid#{00001124-…}`（蓝牙 HID 服务 GUID，Classic）、
    /// `hid#{00001812-…}`（HID over GATT，BLE）。认错会把有线当成蓝牙、从而发错输出帧。
    /// </summary>
    public static bool IsBluetoothPath(string path) =>
        path.Contains("bth", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("00001124", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("00001812", StringComparison.OrdinalIgnoreCase);

    /// <summary>接口判据：VID:PID + 游戏手柄集合（0001/0005）+ 输入报告够长。</summary>
    public static bool MatchesInterface(HidDeviceInfo info) =>
        info.VendorId == VendorId &&
        info.ProductId == ProductId &&
        info.UsagePage == GamepadUsagePage &&
        info.Usage == GamepadUsage &&
        info.InputReportLength >= ReportLength;

    // ────────────────────────── 帧构造 ──────────────────────────

    /// <summary>构造蓝牙子命令帧（0x01：序号 + 8 字节震动 + 子命令 + 数据）。</summary>
    public static byte[] BuildSubcommandFrame(byte subcommand, ReadOnlySpan<byte> data, byte packetNumber,
        int reportLength = 0)
    {
        int length = reportLength > 0 ? reportLength : Math.Max(ReportLength, 11 + data.Length);
        var frame = new byte[length];
        frame[0] = OutputRumbleAndSubcmd;
        frame[1] = (byte)(packetNumber & 0x0F);
        RumbleNeutral.CopyTo(frame.AsSpan(2));
        frame[10] = subcommand;
        if (!data.IsEmpty)
        {
            data.Slice(0, Math.Min(data.Length, Math.Max(0, frame.Length - 11))).CopyTo(frame.AsSpan(11));
        }

        return frame;
    }

    /// <summary>"切标准全量报告模式"这一帧（子命令 0x03 + 0x30）。</summary>
    public static byte[] BuildSetFullReportModeFrame(byte packetNumber, int reportLength = 0) =>
        BuildSubcommandFrame(SubcmdSetInputReportMode, new[] { InputReportModeFull }, packetNumber, reportLength);

    // ────────────────────────── 解析 ──────────────────────────

    /// <summary>这个报告 ID 是不是带电量字节的"全量报告"（0x30/0x31/0x32/0x33）。</summary>
    public static bool IsFullReport(byte reportId) =>
        reportId is 0x30 or 0x31 or 0x32 or 0x33;

    /// <summary>
    /// 托盘要显示的"充电中"判定 —— **只认 bit4**。
    /// <paramref name="hostPowered"/>（bit0）**不参与**：真机上出现过 bit0=1、bit4=0、档位满，
    /// 手柄实际没在充电（2026-11-02 用户实测指出）。参数保留只为把语义写死在这里，防止以后又拿它兜底。
    /// </summary>
    public static bool? ResolveCharging(bool chargingBit, bool hostPowered, int level) => chargingBit;

    /// <summary>
    /// 从 0x30（及其 0x31/0x32/0x33 变体）报告里解析电量。
    /// `level` 是设备原生档位 0..4；`charging` 是充电位；`hostPowered` 是 USB 供电位。
    /// </summary>
    public static bool TryParseBattery(ReadOnlySpan<byte> report, out int level, out bool charging, out bool hostPowered)
    {
        level = -1;
        charging = false;
        hostPowered = false;

        if (report.Length <= BatteryByteIndex) return false;
        if (!IsFullReport(report[0])) return false;

        byte batt = report[BatteryByteIndex];
        hostPowered = (batt & 0x01) != 0;
        charging = (batt & 0x10) != 0;

        int raw = (batt >> 5) & 0x07;
        if (raw > 4) return false;      // 非法档位：说明位域口径不对（别硬解释成 100%）

        level = raw;
        return true;
    }

    /// <summary>档位 0..4 → 百分比（见 <see cref="LevelPercents"/>）。</summary>
    public static int LevelToPercent(int level) =>
        level >= 0 && level < LevelPercents.Length ? LevelPercents[level] : 0;

    public static string DescribeLevel(int level) => level switch
    {
        0 => "空",
        1 => "低",
        2 => "中",
        3 => "高",
        4 => "满",
        _ => "未知",
    };

    // ────────────────────────── 读电量 ──────────────────────────

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;
        error = string.Empty;
        outcome = ReadOutcome.NoResponse;

        // ① 先看设备是不是已经在推 0x30（Windows/Steam 可能已经设过）
        if (TryAwaitFullReport(AwaitFullReportInitiallyMs, out byte[] report, out string firstError))
        {
            return BuildSample(report, out sample, out error, out outcome);
        }

        _log.Debug($"没等到 0x30 全量报告（{firstError}，最近报告 0x{LastReportId:X2}），发子命令切模式");

        // ② 发子命令 0x03 / 0x30
        byte[] frame = BuildSetFullReportModeFrame(_packetNumber++,
            _transport.Info.OutputReportLength > 0 ? _transport.Info.OutputReportLength : 0);
        if (!_transport.TryWriteOutputReport(frame, out int writeError))
        {
            error = $"写子命令帧失败，Win32 错误 {writeError}" +
                    (writeError == 5 ? "（拒绝访问：需要写权限，或该通道被官方软件独占）" : string.Empty);
            outcome = ReadOutcomes.FromWin32(writeError);
            _log.Warn(error);
            return false;
        }

        ModeRequested = true;
        _log.Debug($"已发『切标准全量报告模式』子命令（0x03/0x30，序号 {(_packetNumber - 1) & 0x0F}），等待 0x30 报告");

        // ③ 等 0x30
        if (TryAwaitFullReport(AwaitFullReportAfterSubcmdMs, out byte[] after, out string afterError))
        {
            return BuildSample(after, out sample, out error, out outcome);
        }

        error = $"切换 0x30 报告模式后仍没收到全量报告（{afterError}，最近报告 0x{LastReportId:X2}）" +
                "。手柄可能已休眠/断开，按一下手柄上的键再试";
        outcome = ReadOutcome.NoResponse;
        _log.Warn(error);
        return false;
    }

    private bool BuildSample(byte[] report, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;
        error = string.Empty;

        if (!TryParseBattery(report, out int level, out bool charging, out bool hostPowered))
        {
            error = $"报告 0x{report[0]:X2} 里没有可用的电量字段（原始 {Hex(report)}）";
            outcome = ReadOutcome.Implausible;
            _log.Warn(error);
            return false;
        }

        int? previousLevel = LastLevel;
        LastLevel = level;
        int percent = LevelToPercent(level);

        // 5 档制：档位变化才记 Info（默认不刷屏，但事后能查"什么时候掉的档"）
        if (previousLevel is not null && previousLevel != level)
        {
            _log.Info($"Switch Pro 电量档位变化：{DescribeLevel(previousLevel.Value)}({LevelToPercent(previousLevel.Value)}%) " +
                      $"→ {DescribeLevel(level)}({percent}%)");
        }

        // 只信 **bit4（充电位）**。⚠️ 不要用 hostPowered 兜底成"充电中"：
        // 2026-11-02 真机帧 `30 E4 81 …` 是 bit0=1、bit4=0、档位 4（满），手柄实际**没在充电**，
        // 兜底会把它显示成"已充满 · 充电中"，被用户当场指出（"两个手柄都没有在充电中"）。
        // hostPowered 只作为诊断信息保留在 RawHex/日志里，不参与充电判定。
        bool? chargingFlag = ResolveCharging(charging, hostPowered, level);

        sample = new BatterySample(
            Percent: percent,
            RawValue: report[BatteryByteIndex],
            Charging: chargingFlag,
            Timestamp: DateTimeOffset.Now,
            Scale: BatteryScale.Direct,
            RawHex: $"报告 0x{report[0]:X2} 档位 {level}({DescribeLevel(level)}) " +
                    $"充电={charging} USB供电={hostPowered} 原始 {Hex(report)}")
        {
            LevelText = $"{DescribeLevel(level)} {level + 1}/5 档",
            StageLevel = level,
            StageCount = 5,
        };

        error = string.Empty;
        outcome = ReadOutcome.Ok;
        _log.Debug($"Switch Pro 电量：档位 {level}({DescribeLevel(level)}) → {percent}%，" +
                   $"充电={chargingFlag}，USB供电={hostPowered}");
        return true;
    }

    /// <summary>
    /// 在一段时间内反复读 input report，直到收到全量报告（0x30…）为止。
    /// 期间收到的其它报告（0x3F/0x21/0x3E）只记下来供诊断，不当电量用。
    /// </summary>
    private bool TryAwaitFullReport(int timeoutMs, out byte[] report, out string error)
    {
        report = Array.Empty<byte>();
        error = string.Empty;

        var buffer = new byte[Math.Max(ReportLength, (int)_transport.Info.InputReportLength)];
        long deadline = Environment.TickCount64 + timeoutMs;
        int readFailures = 0;
        int lastReadError = 0;

        for (int i = 0; i < MaxReportsPerWait && Environment.TickCount64 < deadline; i++)
        {
            int left = (int)Math.Clamp(deadline - Environment.TickCount64, 1, 60);
            if (!TryNextReport(buffer, left, out int bytesRead, out int readError))
            {
                lastReadError = readError;
                if (++readFailures >= 8) break;
                continue;
            }

            readFailures = 0;
            int actual = Math.Clamp(bytesRead, 0, buffer.Length);
            if (actual == 0) continue;

            LastReportId = buffer[0];
            LastReportHex = Hex(buffer.AsSpan(0, Math.Min(actual, 16)));

            if (!IsFullReport(buffer[0])) continue;

            report = buffer.AsSpan(0, actual).ToArray();
            return true;
        }

        error = lastReadError != 0 ? $"最后一次读 Win32={lastReadError}" : "超时";
        return false;
    }

    private bool TryNextReport(byte[] buffer, int timeoutMs, out int bytesRead, out int readError)
    {
        Array.Clear(buffer);

        if (_interruptReader is not null)
        {
            return _interruptReader.TryReadInputReport(buffer, timeoutMs, out bytesRead, out readError);
        }

        Thread.Sleep(PollGapMs);
        return _transport.TryGetInputReport(buffer, out bytesRead, out readError);
    }

    /// <summary>
    /// 诊断用：采样原始输入报告（按 hex 去重），用来对照真机实际报文 ——
    /// 协议文档对不上时，这几行就是第一手证据（报告 ID、长度、电量字节都能看到）。
    /// </summary>
    public List<string> SampleRawReports(int maxFrames = 6, int timeoutMs = 800)
    {
        var seen = new List<string>();
        var buffer = new byte[Math.Max(ReportLength, (int)_transport.Info.InputReportLength)];
        long deadline = Environment.TickCount64 + timeoutMs;
        int readFailures = 0;

        for (int i = 0; i < MaxReportsPerWait && seen.Count < maxFrames && Environment.TickCount64 < deadline; i++)
        {
            int left = (int)Math.Clamp(deadline - Environment.TickCount64, 1, 60);
            if (!TryNextReport(buffer, left, out int bytesRead, out _))
            {
                if (++readFailures >= 8) break;
                continue;
            }

            readFailures = 0;
            int actual = Math.Clamp(bytesRead, 0, buffer.Length);
            if (actual == 0) continue;

            string hex = Hex(buffer.AsSpan(0, Math.Min(actual, 12)));
            string line = $"0x{buffer[0]:X2} len={actual} {hex}";
            if (!seen.Contains(line)) seen.Add(line);
        }

        return seen;
    }

    private static string Hex(ReadOnlySpan<byte> data) =>
        string.Join(' ', data.ToArray().Select(b => b.ToString("X2")));

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;
        error = "Switch Pro 手柄没有 DPI 可读（只读电量）";
        return false;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        error = "Switch Pro 手柄不支持设置 DPI（只读电量）";
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
