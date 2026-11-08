using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

/// <summary>
/// 高斯 / HELLO GANSS 2.4G 键盘（`05AC:024F`）。**只读电量，不支持 DPI。**
///
/// 帧（Windows 口径，33 字节，首字节是 report ID）：
/// <code>
///   查询：[0]=0x00(report ID) [1]=0x20 [2]=0x01 [3..32]=0
///   应答：[1]=0x20 [2]=0x01 [3]=0x00 [4]=电量%（是否带 report ID 前缀两种都要兼容）
/// </code>
///
/// 认领判据必须同时满足 **VID:PID + usage 0xFF60/0x0061 + 33/33**：
/// 同一个接收器还暴露一个 `0xFF60/0x0061` 但 **65/65** 的集合，那个不能用（技能里的老坑）。
///
/// ⚠️ 查询成功率只有约 50%：750ms 的应答窗口很容易撞上键盘的休眠/唤醒周期。
/// 表现是「44% → 连续几次失败 → 30 秒后又 44%」，所以这里**必须允许重试**。
///
/// ⚠️ `GS3104T-Pro` 与 `XS75T` **同 PID，接收器只报通用名字（`2.4G Dongle`），无法区分型号**。
/// 用户这台是 **XS75T**，所以显示名就按 XS75T（2026-10-27 用户指出）。
/// 若换成一族里的另一个型号，改 <see cref="ModelName"/> 一行即可 —— 协议完全一样，只有名字不同。
/// </summary>
public sealed class GanssKeyboard : IBatteryDevice
{
    public const ushort VendorId = 0x05AC;
    public const ushort ProductId = 0x024F;
    public const ushort VendorUsagePage = 0xFF60;
    public const ushort VendorUsage = 0x0061;

    /// <summary>查询帧与应答的长度（含 report ID）。同接收器的另一个 65/65 集合不能用。</summary>
    public const int ReportLength = 33;

    /// <summary>查询帧头两字节。</summary>
    public const byte Query0 = 0x20;
    public const byte Query1 = 0x01;

    /// <summary>应答里电量字节相对 `20 01` 的偏移（`20 01 00 [3]=电量`）。</summary>
    public const int PercentOffset = 3;

    /// <summary>显示名。协议上 GS3104T-Pro / XS75T 无法区分，用户这台是 XS75T。</summary>
    public const string ModelName = "XS75T";

    /// <summary>一次查询的应答窗口。键盘休眠时经常整窗没有应答，靠重试补。</summary>
    private const int ReplyTimeoutMs = 750;

    /// <summary>
    /// 尝试次数。真机实测（2026-10-27 22:22:21 那份日志）：键盘闲置时**连续两次**整窗无应答，
    /// 隔一轮轮询又能读到 100% —— 说明失败是休眠/唤醒周期，不是设备坏了。
    /// 2 次约 50% 成功率，加到 3 次（最坏 2.25s）能明显减少"托盘里键盘时有时无"。
    /// </summary>
    private const int Attempts = 3;

    /// <summary>
    /// 连续失败多少轮之后，本轮只试 1 次。
    /// 键盘睡了以后每轮都白等 2.25 秒（界面已经显示"休眠·上次"了，多试两次没有任何收益）；
    /// 4 轮 ≈ 4 分钟，正好和托盘判定"陈旧"的阈值对齐。
    /// </summary>
    private const int SleepyAfterFailures = 4;

    /// <summary>用"取最近一条"接口轮询时的间隔。</summary>
    private const int PollGapMs = 2;

    private readonly IHidTransport _transport;
    private readonly IInterruptReader? _interruptReader;
    private readonly ILog _log;
    private bool _disposed;

    /// <summary>连续失败轮数（成功即清零）。用来在设备明显睡着时少试几次，见 <see cref="SleepyAfterFailures"/>。</summary>
    private int _consecutiveFailures;

    /// <summary>是否成功读到过电量。没读到过之前不要省尝试次数 —— 否则永远读不到。</summary>
    private bool _everSucceeded;

    /// <summary>最近一次收到的原始应答（诊断用）。</summary>
    public string LastReplyHex { get; private set; } = string.Empty;

    public GanssKeyboard(IHidTransport transport, ILog? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _interruptReader = transport as IInterruptReader;
        _log = log ?? NullLog.Instance;
        _log.Debug($"打开高斯键盘: {ModelName}，通道 {transport.Info.Path}");
        _log.Debug($"  通道参数: usage={transport.Info.UsagePage:X4}/{transport.Info.Usage:X4} " +
                  $"in={transport.Info.InputReportLength} out={transport.Info.OutputReportLength} " +
                  $"可写={transport.CanWrite} 读方式={ReadMethod}");
    }

    public string DisplayName => ModelName;
    public string DeviceId => $"{VendorId:X4}:{ProductId:X4}";
    public int MaxDpi => 0;
    public bool SupportsDpi => false;
    public bool SupportsBattery => true;
    public bool CanWrite => _transport.CanWrite;

    /// <summary>诊断里显示的读方式。</summary>
    public string ReadMethod => _interruptReader is not null ? "interrupt ReadFile（overlapped）" : "HidD_GetInputReport 轮询";

    /// <summary>接口判据：VID:PID + usage + **33/33**（65/65 的同名集合不认）。</summary>
    public static bool MatchesInterface(HidDeviceInfo info) =>
        info.VendorId == VendorId &&
        info.ProductId == ProductId &&
        info.UsagePage == VendorUsagePage &&
        info.Usage == VendorUsage &&
        info.InputReportLength == ReportLength &&
        info.OutputReportLength == ReportLength;

    /// <summary>查询帧（33 字节，首字节 report ID = 0）。</summary>
    public static byte[] BuildQuery(int reportLength = ReportLength)
    {
        var frame = new byte[Math.Max(reportLength, 3)];
        frame[0] = 0x00;        // report ID
        frame[1] = Query0;
        frame[2] = Query1;
        return frame;
    }

    /// <summary>
    /// 从应答里取电量百分比。带不带 report ID 前缀都能认：找 `20 01` 这对标记，
    /// 其后第 3 个字节就是百分比。找不到或值不合理都返回 false（交给重试）。
    /// </summary>
    public static bool TryParseReply(ReadOnlySpan<byte> reply, out int percent)
    {
        percent = 0;

        for (int i = 0; i + PercentOffset < reply.Length; i++)
        {
            if (reply[i] != Query0 || reply[i + 1] != Query1) continue;

            int value = reply[i + PercentOffset];
            if (value > 100) continue;   // 不像电量（可能是别的 20 01 帧），继续往后找

            percent = value;
            return true;
        }

        return false;
    }

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;
        error = string.Empty;
        outcome = ReadOutcome.NoResponse;

        if (!_transport.CanWrite)
        {
            error = "该句柄不可写（只读/零权限），高斯键盘要写 output report 才能查询电量";
            outcome = ReadOutcome.NotWritable;
            return false;
        }

        byte[] request = BuildQuery(_transport.Info.OutputReportLength);
        int lastReadError = 0;
        string lastReply = string.Empty;
        bool sawAnyFrame = false;

        // 只有"读到过、又睡了"才省尝试次数；从没读到过时必须每次都全力试
        int attempts = _everSucceeded && _consecutiveFailures >= SleepyAfterFailures ? 1 : Attempts;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            if (!_transport.TryWriteOutputReport(request, out int writeError))
            {
                error = $"写查询帧失败，Win32 错误 {writeError}";
                outcome = ReadOutcomes.FromWin32(writeError);
                _log.Error(error);
                return false;
            }

            var buffer = new byte[Math.Max((int)_transport.Info.InputReportLength, ReportLength)];
            long deadline = Environment.TickCount64 + ReplyTimeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                int left = (int)Math.Clamp(deadline - Environment.TickCount64, 1, 60);
                if (!TryNextReport(buffer, left, out int bytesRead, out lastReadError))
                {
                    continue;
                }

                int actual = Math.Clamp(bytesRead, 0, buffer.Length);
                if (actual < PercentOffset + 1) continue;

                sawAnyFrame = true;
                ReadOnlySpan<byte> span = buffer.AsSpan(0, actual);
                lastReply = Convert.ToHexString(span);

                if (!TryParseReply(span, out int percent)) continue;

                _consecutiveFailures = 0;
                _everSucceeded = true;
                LastReplyHex = lastReply;
                byte raw = (byte)percent;
                sample = new BatterySample(percent, raw, null, DateTimeOffset.Now,
                    BatteryScale.Direct, lastReply);
                error = string.Empty;
                outcome = ReadOutcome.Ok;
                _log.Debug($"高斯键盘电量：raw={raw} → {percent}%（设备直接报百分比），应答 {lastReply}");
                return true;
            }

            _log.Debug($"高斯键盘第 {attempt}/{attempts} 次没等到有效应答" +
                       $"（最后一次读 Win32={lastReadError}，最后收到的报文 {lastReply}）");
        }

        _consecutiveFailures++;

        // 收到了报文但没有一条是电量帧 → 内容不可信（多半也是休眠）；整窗没报文 → 无应答。
        outcome = sawAnyFrame ? ReadOutcome.Implausible : ReadOutcome.NoResponse;

        // 成功率约 50%，一次失败不代表设备坏了 —— 错误里写清重试情况，便于上层决定是否再等一轮
        error = $"查电量没应答（试了 {attempts} 次，最后一次读 Win32={lastReadError}" +
                (lastReply.Length > 0 ? $"，最后收到的报文 {lastReply}" : "，整窗没收到报文") + "）";
        return false;
    }

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;
        error = "高斯键盘没有 DPI 可读（只读电量）";
        return false;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        error = "高斯键盘不支持设置 DPI（只读电量）";
        return false;
    }

    /// <summary>等一条 input report：能用 interrupt 读就用（更可靠），否则退回"取最近一条"轮询。</summary>
    private bool TryNextReport(byte[] buffer, int timeoutMs, out int bytesRead, out int readError)
    {
        Array.Clear(buffer);
        buffer[0] = 0x00;   // report ID

        if (_interruptReader is not null)
        {
            return _interruptReader.TryReadInputReport(buffer, timeoutMs, out bytesRead, out readError);
        }

        Thread.Sleep(PollGapMs);
        return _transport.TryGetInputReport(buffer, out bytesRead, out readError);
    }

    public void Dispose() => _disposed = true;

    /// <summary>诊断用：是否已释放（保持与其它设备类一致的形状）。</summary>
    public bool IsDisposed => _disposed;
}
