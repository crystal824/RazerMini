using RazerMini.Core.Hid;

namespace RazerMini.Core.Devices;

/// <summary>
/// 漫步者的**帧层**：构造、校验和、载荷定位、应答解析、一问一答交换、会话初始化。
/// 拆出来的原因：这一层和"电量/DPI 业务"没有关系，混在一起时 1040 行的文件很难看出协议形状。
/// </summary>
public sealed partial class EdifierHecate
{
    /// <summary>
    /// 构造 64 字节请求帧（Windows 缓冲区口径，[0] 是 report ID）。
    /// `[1]` 由 <see cref="Checksum"/> 自动算好——**校验和错了设备会静默丢帧**。
    /// </summary>
    public static byte[] BuildRequest(byte command, byte length, byte offset,
        ReadOnlySpan<byte> data = default)
    {
        var frame = new byte[ReportLength];
        frame[0] = ReportId;      // [0] report ID
        frame[2] = 0x00;          // [2] flags
        frame[3] = command;       // [3] 命令
        frame[4] = length;        // [4] 长度
        frame[5] = offset;        // [5] 偏移

        if (!data.IsEmpty)
        {
            data.Slice(0, Math.Min(data.Length, ReportLength - PayloadIndex))
                .CopyTo(frame.AsSpan(PayloadIndex));
        }

        frame[1] = Checksum(frame);   // [1] 校验和，必须最后算
        return frame;
    }

    /// <summary>
    /// 帧校验和：`[3]` 到末尾所有字节之和取低 8 位。
    /// 抓包逐条验算过：`04 1b 00 03 18`（`03+18=1b`）、`04 20 00 1a 06`（`1a+06=20`）、
    /// `04 2d 00 28 01 00 00 00 04`（`28+01+04=2d`）、24 字节配置块 `79 02 06 18 …`（正好 0x79）。
    /// </summary>
    public static byte Checksum(ReadOnlySpan<byte> frame)
    {
        int sum = 0;
        for (int i = ChecksumFrom; i < frame.Length; i++) sum += frame[i];
        return (byte)(sum & 0xFF);
    }

    /// <summary>
    /// 定位载荷起点 <c>p</c>：报文带 report ID 时 p=1，被驱动摘掉时 p=0。
    /// 后面所有偏移都写成 <c>p + 相对偏移</c>，两种口径共用一套下标。
    /// </summary>
    private static bool TryLocatePayload(ReadOnlySpan<byte> frame, out int p)
    {
        p = -1;
        if (frame.Length < ReportLength - 1) return false;
        p = frame[0] == ReportId ? 1 : 0;
        return true;
    }

    private static byte CmdOf(ReadOnlySpan<byte> frame, int p) => frame[p + 2];

    /// <summary>请求的校验和（设备在应答里原样回显）。</summary>
    private static byte EchoOf(ReadOnlySpan<byte> frame, int p) => frame[p];

    private static byte DataAt(ReadOnlySpan<byte> frame, int p, int index) => frame[p + DataOffset + index];

    /// <summary>
    /// 解析主动查询的电量应答：`[8]`=百分比、`[9]`=充电标志。
    /// 校验和必须对得上，否则可能是上一条命令的陈旧应答。
    /// </summary>
    public static bool TryParseBatteryReply(ReadOnlySpan<byte> frame, byte echo, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;

        if (!TryLocatePayload(frame, out int p)) return false;
        if (EchoOf(frame, p) != echo) return false;
        if (CmdOf(frame, p) != CmdBattery) return false;

        int raw = DataAt(frame, p, 0);
        bool flag = DataAt(frame, p, 1) != 0x00;

        percent = Math.Clamp(raw, 0, 100);
        charging = flag;
        if (percent >= 100) charging = true;   // 满电兜底（满电也可能报 0x01）
        return true;
    }

    /// <summary>
    /// 解析被动状态帧（`04 20 00 1A …`）。主动查询不通时的兜底路径。
    /// 兼容"含 report ID"与"驱动把 report ID 摘掉"两种偏移。
    /// </summary>
    public static bool TryParseStatusFrame(ReadOnlySpan<byte> frame, out int percent, out bool charging)
    {
        percent = 0;
        charging = false;

        int p = -1;
        if (frame.Length >= 12 && frame[0] == ReportId && frame[1] == StatusLead &&
            frame[2] == 0x00 && frame[3] == StatusLenByte)
        {
            p = 1;
        }
        else if (frame.Length >= 11 && frame[0] == StatusLead && frame[1] == 0x00 && frame[2] == StatusLenByte)
        {
            p = 0;
        }

        if (p < 0) return false;
        if (frame.Length < p + DataOffset + 2) return false;

        int raw = DataAt(frame, p, 0);
        byte chargeFlag = DataAt(frame, p, 1);

        percent = Math.Clamp(raw, 0, 100);
        charging = chargeFlag != 0x00;
        if (percent >= 100) charging = true;
        return true;
    }

    /// <summary>对外的一步：没做会话初始化就先补上，然后发一帧。</summary>
    private bool TryExchange(byte command, byte length, byte offset, ReadOnlySpan<byte> data,
        out byte[] reply, out string error, out ReadOutcome outcome)
    {
        if (!_sessionReady && command != CmdHeartbeat)
        {
            EnsureSession();
        }

        return ExchangeRaw(command, length, offset, data, ExchangeTimeoutMs, out reply, out error, out outcome);
    }

    /// <summary>
    /// 一次裸交换（不做会话初始化，避免递归）：写 64 字节 output report → 读 input report 直到拿到
    /// 「校验和回显 + 命令」都对得上的应答。先按校验和严格匹配；超过 <see cref="CmdOnlyFallbackMs"/>
    /// 只按命令匹配（设备一定会回显命令字节）。
    /// </summary>
    private bool ExchangeRaw(byte command, byte length, byte offset, ReadOnlySpan<byte> data,
        int timeoutMs, out byte[] reply, out string error, out ReadOutcome outcome, int sends = Sends)
    {
        reply = Array.Empty<byte>();
        error = string.Empty;
        outcome = ReadOutcome.NoResponse;

        if (!_transport.CanWrite)
        {
            error = "该句柄不可写（只读/零权限），漫步者主动查询需要读写权限";
            outcome = ReadOutcome.NotWritable;
            return false;
        }

        if (_transport.Info.OutputReportLength < PayloadIndex)
        {
            error = $"通道没有可用的 output report（OutputReportLength={_transport.Info.OutputReportLength}）";
            outcome = ReadOutcome.Unsupported;
            return false;
        }

        byte[] request = BuildRequest(command, length, offset, data);
        _lastEcho = request[1];
        if (request.Length != _transport.Info.OutputReportLength)
        {
            Array.Resize(ref request, _transport.Info.OutputReportLength);
        }

        var buffer = new byte[Math.Max(ReportLength, (int)_transport.Info.InputReportLength)];
        int lastReadError = 0;

        for (int send = 1; send <= sends; send++)
        {
            if (!_transport.TryWriteOutputReport(request, out int writeError))
            {
                error = $"写 output report 失败，Win32 错误 {writeError}" +
                        (writeError == 5 ? "（拒绝访问：需要读写权限，或被官方软件占着）" : string.Empty);
                outcome = ReadOutcomes.FromWin32(writeError);
                return false;
            }

            long sentAt = Environment.TickCount64;
            byte[]? commandOnly = null;

            while (Environment.TickCount64 - sentAt < timeoutMs)
            {
                int left = (int)Math.Clamp(timeoutMs - (Environment.TickCount64 - sentAt), 1, 60);
                if (!TryNextReport(buffer, left, out int bytesRead, out lastReadError))
                {
                    continue;
                }

                int actual = Math.Clamp(bytesRead, 0, buffer.Length);
                if (actual < PayloadIndex) continue;

                ReadOnlySpan<byte> span = buffer.AsSpan(0, actual);
                if (!TryLocatePayload(span, out int p)) continue;
                if (CmdOf(span, p) != command) continue;

                if (EchoOf(span, p) == _lastEcho)
                {
                    reply = (byte[])buffer.Clone();
                    outcome = ReadOutcome.Ok;
                    return true;
                }

                // 命令对得上但校验和回显不同：先记下，等够时间再用
                commandOnly ??= (byte[])buffer.Clone();
                if (commandOnly is not null && Environment.TickCount64 - sentAt >= CmdOnlyFallbackMs)
                {
                    reply = commandOnly;
                    outcome = ReadOutcome.Ok;
                    _log.Debug($"漫步者命令 0x{command:X2} 只按命令匹配到应答（校验和回显不符）");
                    return true;
                }
            }

            if (commandOnly is not null)
            {
                reply = commandOnly;
                outcome = ReadOutcome.Ok;
                return true;
            }

            _log.Debug($"漫步者命令 0x{command:X2} 第 {send}/{sends} 次没应答，重发");
            Thread.Sleep(10);
        }

        error = $"命令 0x{command:X2}（长度 {length}，偏移 {offset}）连续 {sends} 次没应答" +
                $"（读方式：{ReadMethod}，最后一次读 Win32={lastReadError}{ExplainReadError(lastReadError)}）";
        outcome = ReadOutcomes.FromWin32(lastReadError);
        if (outcome == ReadOutcome.Failure) outcome = ReadOutcome.NoResponse;   // 1460 超时也走这里
        return false;
    }

    /// <summary>
    /// 读下一条 input report：有 <see cref="IInterruptReader"/> 就阻塞读（官方 App 的做法），
    /// 否则退回"取最近一条"的轮询。
    /// </summary>
    private bool TryNextReport(byte[] buffer, int timeoutMs, out int bytesRead, out int readError)
    {
        Array.Clear(buffer);
        buffer[0] = ReportId;

        if (_interruptReader is not null)
        {
            return _interruptReader.TryReadInputReport(buffer, timeoutMs, out bytesRead, out readError);
        }

        Thread.Sleep(PollGapMs);
        return _transport.TryGetInputReport(buffer, out bytesRead, out readError);
    }

    /// <summary>
    /// 会话初始化：把官方 App 打开设备时跑的那一段（<see cref="SessionSequence"/>）原样重放一遍。
    ///
    /// 为什么必须做：真机只发 `0x1A` 时设备**只回 `0xAA`**，其它一条都不理；而我们的帧与抓包
    /// 逐字节相同、校验和也对 —— 差别只在"设备还没进会话"。每帧应答都打在 Debug 日志里，
    /// 从哪一帧开始有回应一眼可见。失败不阻断，继续按正常流程发命令。
    /// </summary>
    private void EnsureSession()
    {
        if (_sessionReady || !_transport.CanWrite) return;
        _sessionReady = true;   // 先置位，防止重入

        int answered = 0;
        for (int pass = 1; pass <= SessionMaxPasses; pass++)
        {
            answered = 0;
            bool lastAnswered = false;

            for (int i = 0; i < SessionSequence.Length; i++)
            {
                (byte command, byte length, byte offset) = SessionSequence[i];
                bool ok = ExchangeRaw(command, length, offset, ReadOnlySpan<byte>.Empty,
                    SessionFrameTimeoutMs, out byte[] reply, out string error, out _, sends: 1);

                string detail = ok
                    ? "→ " + Convert.ToHexString(reply.AsSpan(0, Math.Min(10, reply.Length)))
                    : error;
                _log.Debug($"漫步者会话[第 {pass} 遍] 0x{command:X2}/L{length:X2}/O{offset:X2} " +
                           $"{(ok ? "✓" : "✗")} {detail}");
                if (ok) answered++;
                lastAnswered = ok;
            }

            // 末帧（页 0x05 最后一块）有应答 = 设备已经醒了、会话可用
            if (lastAnswered)
            {
                _log.Info($"漫步者会话初始化完成：第 {pass} 遍 {answered}/{SessionSequence.Length} 帧有应答");
                return;
            }

            _log.Debug($"漫步者会话第 {pass} 遍末帧无应答（{answered}/{SessionSequence.Length}），" +
                       "设备多半还在休眠，再来一遍");
            Thread.Sleep(50);
        }

        _log.Warn($"漫步者会话初始化 {SessionMaxPasses} 遍都没等到末帧应答" +
                  $"（最后一遍 {answered}/{SessionSequence.Length} 帧）");
    }

    /// <summary>
    /// `0x01` = 开一次配置会话。官方 App 每次读写配置前都会发它（抓包：8373 / 8412 / 13962），
    /// 写完再发 `0x02` 收尾。**不是可选项** —— 没有它会话会失效，设备读页只回空帧。
    /// </summary>
    private void TryBeginConfigBurst() =>
        ExchangeRaw(0x01, 0x00, 0x00, ReadOnlySpan<byte>.Empty, SessionFrameTimeoutMs,
            out _, out _, out _, sends: 2);

    /// <summary>`0x02` = 结束配置会话。</summary>
    private void TryEndConfigBurst() =>
        ExchangeRaw(0x02, 0x00, 0x00, ReadOnlySpan<byte>.Empty, SessionFrameTimeoutMs,
            out _, out _, out _, sends: 2);
}
