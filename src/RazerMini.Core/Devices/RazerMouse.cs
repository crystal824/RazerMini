using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

/// <summary>
/// 雷蛇鼠标驱动（91 字节 feature report 通道）。
/// 所有失败分支都返回可读原因并写日志 —— 禁止静默返回（HANDOFF 第七节第 3 条）。
/// </summary>
public sealed class RazerMouse : IBatteryDevice
{
    // 与 openrazer razer_send_payload 一致：最多 5 次，间隔 10ms
    private const int MaxAttempts = 5;
    private const int CommandSettleMs = 10;     // SET_FEATURE 之后给设备一点时间准备应答
    private const int BusyRetryDelayMs = 10;

    private readonly IHidTransport _transport;
    private readonly RazerMouseModel _model;
    private readonly ILog _log;
    private readonly int _reportLength;
    private readonly List<string> _trace = new();
    private bool _disposed;

    /// <summary>是否额外查充电状态（多一条命令）。托盘层按配置设置。</summary>
    public bool ReadChargingStatus { get; set; } = true;

    /// <summary>最近若干次请求/应答的原始字节，诊断用（--diag 会打出来）。</summary>
    public IReadOnlyList<string> Trace
    {
        get { lock (_trace) return _trace.ToArray(); }
    }

    public RazerMouse(IHidTransport transport, RazerMouseModel model, ILog? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _log = log ?? NullLog.Instance;
        _reportLength = transport.Info.FeatureReportLength;

        if (_reportLength < RazerReport.Length)
        {
            throw new ArgumentException(
                $"该接口 feature report 只有 {_reportLength} 字节，雷蛇协议需要 >= {RazerReport.Length} 字节", nameof(transport));
        }

        _log.Debug($"打开雷蛇设备: {model.Name} [{model.Id}]，通道 {transport.Info.Path}");
        _log.Debug($"  通道参数: usage={transport.Info.UsagePage:X4}/{transport.Info.Usage:X4} " +
                  $"feature={_reportLength}B 事务号=0x{model.TransactionId:X2} 最大DPI={model.MaxDpi}");
    }

    public string DisplayName => _model.Name;
    public string DeviceId => _model.Id;
    public int MaxDpi => _model.MaxDpi;
    public bool SupportsDpi => _model.SupportsDpi;
    public bool SupportsBattery => _model.SupportsBattery;

    /// <summary>句柄是否可写（决定 DPI 能不能改）。</summary>
    public bool CanWrite => _transport.CanWrite;

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;

        if (!_model.SupportsBattery)
        {
            error = $"{_model.Name} 没有电量命令";
            outcome = ReadOutcome.Unsupported;
            _log.Warn(error);
            return false;
        }

        var request = RazerCommands.GetBatteryLevel(_model.TransactionId);
        if (!Exchange(request, "读电量", out RazerResponse response, out error, out outcome))
        {
            return false;
        }

        byte raw = response.Arg(1);
        int percent = scale switch
        {
            BatteryScale.Direct => Math.Clamp((int)raw, 0, 100),
            _ => (int)Math.Clamp((raw * 100 + 127) / 255, 0, 100),
        };

        // 充电状态是独立的一条命令；失败不影响电量结论（充电状态记为未知）
        bool? charging = null;
        if (ReadChargingStatus)
        {
            if (TryReadCharging(out bool chargingValue, out string chargingError))
            {
                charging = chargingValue;
            }
            else
            {
                _log.Warn($"充电状态读取失败（电量仍有效）：{chargingError}");
            }
        }

        sample = new BatterySample(
            Percent: percent,
            RawValue: raw,
            Charging: charging,
            Timestamp: DateTimeOffset.Now,
            Scale: scale,
            RawHex: RazerReport.ToHex(response.Arguments.AsSpan(0, 4)));

        _log.Debug($"电量: raw={raw} → {percent}%（{scale}），充电={charging?.ToString() ?? "未知"}，" +
                   $"应答 args[0..3]={sample.RawHex}");
        error = string.Empty;
        outcome = ReadOutcome.Ok;
        return true;
    }

    private bool TryReadCharging(out bool charging, out string error)
    {
        charging = false;
        var request = RazerCommands.GetChargingStatus(_model.TransactionId);
        if (!Exchange(request, "读充电状态", out RazerResponse response, out error, out _))
        {
            return false;
        }

        byte raw = response.Arg(1);
        charging = raw != 0x00;
        error = string.Empty;
        return true;
    }

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;

        if (!_model.SupportsDpi)
        {
            error = $"{_model.Name} 没有 DPI 命令";
            _log.Warn(error);
            return false;
        }

        var request = RazerCommands.GetDpi(_model.TransactionId);
        if (!Exchange(request, "读 DPI", out RazerResponse response, out error, out _))
        {
            return false;
        }

        // 大端：x = args[1..2]，y = args[3..4]（整包 byte 10..13）
        ushort x = response.ArgUInt16BigEndian(1);
        ushort y = response.ArgUInt16BigEndian(3);

        if (x == 0 || y == 0)
        {
            error = $"DPI 应答异常（解析出 {x}x{y}），原始 args={RazerReport.ToHex(response.Arguments.AsSpan(0, 6))}";
            _log.Warn(error);
            return false;
        }

        dpiX = x;
        dpiY = y;
        _log.Debug($"DPI: {x}x{y}（应答 args[1..4]={RazerReport.ToHex(response.Arguments.AsSpan(1, 4))}）");
        error = string.Empty;
        return true;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        if (!_model.SupportsDpi)
        {
            error = $"{_model.Name} 没有 DPI 命令";
            _log.Warn(error);
            return false;
        }

        if (!_transport.CanWrite)
        {
            // 零权限/只读句柄不代表一定写不了：HID 的 feature IOCTL 是 FILE_ANY_ACCESS，
            // 真机实测零权限句柄能成功 SET_FEATURE（2026-10-26）。这里只告警，不拦。
            _log.Warn("当前句柄是零权限/只读打开的，仍试着发一次 SET_FEATURE（可能成功）");
        }

        int clampedX = Math.Clamp(dpiX, 100, _model.MaxDpi);
        int clampedY = Math.Clamp(dpiY, 100, _model.MaxDpi);
        if (clampedX != dpiX || clampedY != dpiY)
        {
            _log.Warn($"DPI {dpiX}x{dpiY} 超出范围，已夹到 {clampedX}x{clampedY}（范围 100-{_model.MaxDpi}）");
        }

        var request = RazerCommands.SetDpi(_model.TransactionId, (ushort)clampedX, (ushort)clampedY);
        if (!Exchange(request, "写 DPI", out RazerResponse response, out error, out _))
        {
            return false;
        }

        if (!response.IsSuccess)
        {
            error = $"写 DPI 被拒绝：{response.StatusText}（txn=0x{_model.TransactionId:X2}）";
            _log.Warn(error);
            return false;
        }

        _log.Info($"DPI 已写入 {clampedX}x{clampedY}（txn=0x{_model.TransactionId:X2}）");
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 一次完整的 SET_FEATURE + GET_FEATURE 交换，带"忙"重试和应答错配检测。
    /// 单位：<see cref="RazerReport.Length"/> 字节帧；通道缓冲区可能更长，尾部补零。
    /// </summary>
    private bool Exchange(byte[] frame, string what, out RazerResponse response, out string error,
        out ReadOutcome outcome)
    {
        response = default;
        error = string.Empty;
        outcome = ReadOutcome.Failure;
        string lastProblem = "未执行";
        RazerResponse? busyCandidate = null;   // 只有 busy 应答时按 openrazer 口径兜底接受

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (_disposed)
            {
                error = "设备已释放";
                return false;
            }

            var buffer = new byte[_reportLength];
            frame.CopyTo(buffer, 0);

            if (!_transport.TrySetFeature(buffer, out int setError))
            {
                lastProblem = $"SET_FEATURE 失败，Win32 错误 {setError}（{DescribeWin32(setError)}）";
                outcome = ReadOutcomes.FromWin32(setError);
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                Thread.Sleep(BusyRetryDelayMs);
                continue;
            }

            Thread.Sleep(CommandSettleMs);

            if (!_transport.TryGetFeature(buffer, out int getError))
            {
                lastProblem = $"GET_FEATURE 失败，Win32 错误 {getError}（{DescribeWin32(getError)}）";
                outcome = ReadOutcomes.FromWin32(getError);
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                Thread.Sleep(BusyRetryDelayMs);
                continue;
            }

            RazerResponse parsed;
            try
            {
                parsed = RazerReport.Parse(buffer);
            }
            catch (Exception ex)
            {
                lastProblem = $"应答解析异常：{ex.Message}";
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                continue;
            }

            // 应答错配检测：class/id 必须对得上，否则是上一次命令的残留应答
            if (!parsed.IsSuccess && parsed.CommandClass == 0 && parsed.CommandId == 0)
            {
                lastProblem = $"设备回的是空帧（status={parsed.StatusText}），可能通道不支持该命令或事务号不对";
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                continue;
            }

            if (parsed.CommandClass != frame[7] || parsed.CommandId != frame[8])
            {
                lastProblem = $"应答错配：期望 class=0x{frame[7]:X2} id=0x{frame[8]:X2}，" +
                              $"收到 class=0x{parsed.CommandClass:X2} id=0x{parsed.CommandId:X2}（残留应答）";
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                Thread.Sleep(BusyRetryDelayMs);
                continue;
            }

            if (parsed.Status == RazerReport.StatusBusy)
            {
                // openrazer razer_send_payload：busy 视为成功（有些命令回了 busy 但数据/写入已经生效），
                // 但先重试争取拿到明确的成功应答，重试用尽再按 busy 接受。
                busyCandidate = parsed;
                lastProblem = "设备忙(0x01)";
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}，稍后重试");
                Thread.Sleep(BusyRetryDelayMs);
                continue;
            }

            if (!parsed.IsSuccess)
            {
                lastProblem = $"设备返回 {parsed.StatusText}{StatusHint(parsed.Status)}";
                outcome = parsed.Status switch
                {
                    RazerReport.StatusNoResponse => ReadOutcome.NoResponse,
                    RazerReport.StatusNotSupported => ReadOutcome.Unsupported,
                    _ => ReadOutcome.Failure,
                };
                _log.Warn($"{what} 第 {attempt}/{MaxAttempts} 次：{lastProblem}");
                // 无应答/不支持重试也没用，直接失败
                if (parsed.Status is RazerReport.StatusNoResponse or RazerReport.StatusNotSupported)
                {
                    break;
                }
                continue;
            }

            response = parsed;
            error = string.Empty;
            outcome = ReadOutcome.Ok;
            AddTrace($"{what} 成功: 请求 {Head(frame)} → 应答 status=0x{parsed.Status:X2} " +
                     $"args={RazerReport.ToHex(parsed.Arguments.AsSpan(0, 8))}");
            return true;
        }

        // 重试用尽仍是 busy：按 openrazer 口径接受（busy 常常意味着数据/写入已经生效）
        if (busyCandidate is { } busy)
        {
            response = busy;
            error = string.Empty;
            outcome = ReadOutcome.Ok;
            _log.Warn($"{what}：设备始终回 busy(0x01)，按 openrazer 口径视为成功（args={RazerReport.ToHex(busy.Arguments.AsSpan(0, 4))}）");
            AddTrace($"{what} busy兜底: 请求 {Head(frame)} → 应答 status=0x01 " +
                     $"args={RazerReport.ToHex(busy.Arguments.AsSpan(0, 8))}");
            return true;
        }

        error = $"{what}失败：{lastProblem}";
        _log.Error(error);
        AddTrace($"{what} 失败: 请求 {Head(frame)} → {lastProblem}");
        return false;
    }

    private static string Head(byte[] frame) =>
        RazerReport.ToHex(frame.AsSpan(0, 14));

    /// <summary>
    /// 把常见状态码翻译成"该动哪里"的提示。
    /// 真机 2026-10-27：007B 接收器一直回 0x04 —— 接收器在线、**鼠标在休眠/关机**，
    /// 这种情况下重试没有意义（唤醒只能靠移动鼠标），报错要直接说清。
    /// </summary>
    private static string StatusHint(byte status) => status switch
    {
        RazerReport.StatusNoResponse =>
            "（接收器在线、鼠标没有应答：休眠/关机/未配对。动一下鼠标，再点「立即刷新」）",
        RazerReport.StatusNotSupported =>
            "（该型号固件没有这条命令，例如 007E 充电底座的 91B 通道）",
        RazerReport.StatusFailure => "（设备执行失败：当前模式不支持或需要官方软件配合）",
        _ => string.Empty,
    };

    private void AddTrace(string line)
    {
        lock (_trace)
        {
            _trace.Add($"{DateTime.Now:HH:mm:ss.fff} {line}");
            if (_trace.Count > 24) _trace.RemoveAt(0);
        }
    }

    private static string DescribeWin32(int code) => code switch
    {
        0 => "无错误",
        5 => "拒绝访问（可能被雷蛇官方软件占用）",
        6 => "无效句柄（设备已拔出）",
        23 => "数据错误（CRC/长度不符）",
        87 => "参数错误（缓冲区长度与 report 长度不符）",
        1167 => "设备未连接",
        1168 => "找不到元素",
        _ => "见 Win32 错误码表",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
