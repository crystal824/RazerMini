using System.Diagnostics;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

/// <summary>
/// 雷柏 VT9 Pro（`24AE:1205`，一代 0xBA 协议）。
///
/// 帧（Windows 口径，output report 首字节是 report ID）：
/// <code>
///   [0]=0xBA(report ID) [1]=conn [2]=cmd [3]=len [4..7]=addr(LE32) [8..]=data
///   conn: 0xA5 = 2.4G 接收器，0xFF = 有线（两个都试，谁应答算谁）
///   cmd : 0xAA = 电量；0xA4 = 读 EEPROM；0xA5 = 写 EEPROM
/// </code>
///
/// ⚠️ **应答不是一问一答**（mousectl 逆向官方驱动的结论）：input report 会一直保留上一次的答案，
/// dongle 收到新帧先回 `busy(0x02)`，radio 往返后再回 `0x01` + 新数据。所以 "OK 只有在见过 busy
/// 之后才算数"，一直没见到 busy 说明这帧丢了、要重发。电量应答 `[1]` = 充电态（1 放电 / 2 充电），
/// **`0` 表示这是 EEPROM 块应答、不是电量应答**（可识别陈旧应答）。
///
/// DPI 存在 EEPROM 块里（7 档 × u16 LE）：
/// <code>
///   0x888 = X 表（16 字节：7×u16）   0x8C8 = Y 表（同布局）   0x898 = 当前档位（[0] 为 0 基下标）
/// </code>
/// ⚠️ mousectl 把 <c>[14]</c> 标成"档数字节"，但真机上它一会是 **0**、一会是 **2**（7 档始终都在），
/// 两次都因此把合法写入误判成"地址猜错"。判据改成只看"当前档位的值是否像 DPI"，`[14]`/`[15]` 完全忽略。
///
/// 写入必须先读、校验"这块确实像 DPI 表"再写，写完读回校验 —— 地址若不对宁可拒绝，也不能写坏配置。
/// </summary>
public sealed class RapooVt9Pro : IBatteryDevice
{
    public const ushort VendorId = 0x24AE;
    public const ushort ProductId = 0x1205;
    public const ushort VendorUsagePage = 0xFF00;
    public const ushort VendorUsage = 0x000E;
    public const byte ReportId = 0xBA;

    public const byte CmdBattery = 0xAA;
    public const byte CmdReadBlock = 0xA4;
    public const byte CmdWriteBlock = 0xA5;
    public const byte AckOk = 0x01;

    public const uint AddrDpiX = 0x888;
    public const uint AddrDpiY = 0x8C8;
    public const uint AddrDpiCur = 0x898;
    public const int DpiBlockLength = 16;
    public const int DpiCurBlockLength = 4;
    public const int StageCount = 7;
    public const int MinDpi = 50;
    public const int MaxDpiValue = 26000;
    public const int DpiStep = 50;

    public const string ModelName = "VT9 Pro";

    /// <summary>2.4G 接收器优先，然后有线。</summary>
    public static readonly byte[] ConnBytes = { 0xA5, 0xFF };

    private const int FrameLength = 32;
    private const int MaxSends = 4;
    private const int StaleRetries = 4;
    private const int PollGapMs = 2;
    private const int BusyAcceptMs = 100;
    private const int ExchangeTimeoutMs = 1500;
    private const int SendGapMs = 50;

    /// <summary>块读重试次数：设备休眠时接收器会 ACK 一个"全 0 块"，这不是真实数据。</summary>
    private const int BlockReadAttempts = 3;

    private const int BlockRetryDelayMs = 60;

    private readonly IHidTransport _transport;
    private readonly ILog _log;
    private byte? _workingConn;
    private bool _disposed;

    /// <summary>最近一次读到的 X 表原文（诊断用，16 字节 hex）。</summary>
    public string LastXTableHex { get; private set; } = string.Empty;

    /// <summary>最近一次读到的 Y 表原文（诊断用）。</summary>
    public string LastYTableHex { get; private set; } = string.Empty;

    /// <summary>最近一次读到的"当前档位"块原文（诊断用）。</summary>
    public string LastStageHex { get; private set; } = string.Empty;

    public RapooVt9Pro(IHidTransport transport, ILog? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _log = log ?? NullLog.Instance;
        _log.Debug($"打开雷柏设备: {ModelName}，通道 {transport.Info.Path}");
        _log.Debug($"  通道参数: usage={transport.Info.UsagePage:X4}/{transport.Info.Usage:X4} " +
                  $"in={transport.Info.InputReportLength} out={transport.Info.OutputReportLength} reportId=0x{ReportId:X2}");
    }

    public string DisplayName => ModelName;
    public string DeviceId => $"{VendorId:X4}:{ProductId:X4}";
    public int MaxDpi => MaxDpiValue;
    public bool SupportsDpi => true;
    public bool SupportsBattery => true;
    public bool CanWrite => _transport.CanWrite;

    public static bool MatchesInterface(HidDeviceInfo info) =>
        info.VendorId == VendorId &&
        info.ProductId == ProductId &&
        info.UsagePage == VendorUsagePage &&
        info.Usage == VendorUsage &&
        info.InputReportLength >= FrameLength &&
        info.OutputReportLength >= FrameLength;

    /// <summary>构造一帧 32 字节请求（Windows 缓冲区，[0] 是 report ID）。</summary>
    public static byte[] BuildFrame(byte conn, byte cmd = CmdBattery, byte length = 0, uint address = 0,
        ReadOnlySpan<byte> data = default)
    {
        var frame = new byte[FrameLength];
        frame[0] = ReportId;
        frame[1] = conn;
        frame[2] = cmd;
        frame[3] = length;
        frame[4] = (byte)(address & 0xFF);
        frame[5] = (byte)((address >> 8) & 0xFF);
        frame[6] = (byte)((address >> 16) & 0xFF);
        frame[7] = (byte)((address >> 24) & 0xFF);

        if (!data.IsEmpty)
        {
            data.Slice(0, Math.Min(data.Length, FrameLength - 8)).CopyTo(frame.AsSpan(8));
        }

        return frame;
    }

    /// <summary>
    /// 解析电量应答。兼容"含 report ID"与"驱动已摘掉 report ID"两种偏移。
    /// ⚠️ 这里只做**结构**解析，不判断内容是否可信 —— 内容校验见 <see cref="IsPlausibleBattery"/>。
    /// 以前把 percent 夹到 0..100，等于替设备编数据（0xFF 会被读成 100%）。
    /// </summary>
    public static bool TryParseResponse(ReadOnlySpan<byte> frame, out byte status, out byte chargeState, out int percent)
    {
        status = 0;
        chargeState = 0;
        percent = 0;

        int offset = frame.Length >= 4 && frame[0] == ReportId ? 1 : 0;
        if (frame.Length < offset + 3) return false;

        status = frame[offset];
        chargeState = frame[offset + 1];
        percent = frame[offset + 2];
        return true;
    }

    /// <summary>
    /// 电量应答的**内容**校验（2026-10-27 真机补）：三条同时成立才算一条电量应答。
    /// <list type="number">
    /// <item>status = 0x01（ACK）；</item>
    /// <item>充电态 = 1（放电）或 2（充电）—— 0 是 EEPROM 块应答的特征，其它值（真机上出现过 <c>0x70</c>）
    /// 说明拿到的是**设备事件帧 / 陈旧残留帧**，不是对电量命令的回答；</item>
    /// <item>百分比 0..100，**不做夹取**（夹取会把假值伪装成合法值）。</item>
    /// </list>
    /// 为什么必须有这道闸：`HidD_GetInputReport` 的语义是"取最后一条 input report"，
    /// 仓库/中断口里可能压着任意残留帧（事件上报、上一条命令的应答）。
    /// 之前只挡了 <c>charge==0</c>，于是 `charge=0x70, percent=0` 的垃圾被当成"真 0%"显示给用户。
    /// </summary>
    public static bool IsPlausibleBattery(byte status, byte chargeState, int percent) =>
        status == AckOk && chargeState is 1 or 2 && percent is >= 0 and <= 100;

    /// <summary>说清"这条应答为什么不可信"，日志与诊断共用。</summary>
    public static string DescribeImplausible(byte status, byte chargeState, int percent) =>
        status != AckOk
            ? $"status=0x{status:X2} 不是 ACK(0x01)"
            : chargeState == 0
                ? "充电态=0（EEPROM 块应答的特征）"
                : chargeState is not (1 or 2)
                    ? $"充电态=0x{chargeState:X2}（只可能是 0x01 放电 / 0x02 充电）"
                    : $"百分比 {percent} 超出 0..100";

    /// <summary>
    /// 解析 EEPROM 块应答：应答头 4 字节（status + 2 未知 + 长度/命令回显），数据从第 5 字节开始。
    /// 偏移与电量应答一致（mousectl: <c>resp[4:4+length]</c>，其中 resp 已摘掉 report ID）。
    /// </summary>
    public static bool TryParseBlockData(ReadOnlySpan<byte> frame, int length, out byte[] data)
    {
        data = Array.Empty<byte>();

        int baseIndex = frame.Length >= 5 + length && frame[0] == ReportId ? 1 : 0;
        if (frame.Length < baseIndex + 4 + length) return false;
        if (frame[baseIndex] != AckOk) return false;

        data = frame.Slice(baseIndex + 4, length).ToArray();
        return true;
    }

    /// <summary>把 DPI 规整到传感器支持的范围内（50–26000，50 步进）。</summary>
    public static int NormalizeDpi(int dpi)
    {
        int clamped = Math.Clamp(dpi, MinDpi, MaxDpiValue);
        int stepped = (int)Math.Round((double)clamped / DpiStep) * DpiStep;
        return Math.Clamp(stepped, MinDpi, MaxDpiValue);
    }

    // ────────────────────────── 电量 ──────────────────────────

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;
        outcome = ReadOutcome.NoResponse;
        // 用可空变量累积"最该采取动作"的失败语义：初值不能是 NoResponse，
        // 否则 Escalate 会把更轻的 Implausible（休眠）吃掉（枚举值是"越大越该动"）。
        ReadOutcome? seen = null;
        var problems = new List<string>();
        bool fatal = false;

        foreach (byte conn in OrderedConns())
        {
            string link = DescribeConn(conn);
            bool answeredHere = false;      // 这条链路回过帧（哪怕内容不可信）
            string previousRaw = string.Empty;

            for (int stale = 1; stale <= StaleRetries; stale++)
            {
                if (!TryExchange(conn, BuildFrame(conn, CmdBattery), out byte[] response, out string requestError,
                        out ReadOutcome exchangeOutcome))
                {
                    problems.Add($"{link}: {requestError}");
                    _log.Warn($"{link} 第 {stale}/{StaleRetries} 次：{requestError}");
                    seen = seen is null ? exchangeOutcome : ReadOutcomes.Escalate(seen.Value, exchangeOutcome);
                    fatal = exchangeOutcome == ReadOutcome.HandleDead;
                    break;
                }

                answeredHere = true;

                if (!TryParseResponse(response, out byte status, out byte charge, out int percent))
                {
                    problems.Add($"{link}: 应答太短，不像电量应答");
                    seen = seen is null ? ReadOutcome.Implausible : ReadOutcomes.Escalate(seen.Value, ReadOutcome.Implausible);
                    break;
                }

                if (!IsPlausibleBattery(status, charge, percent))
                {
                    // 不能当电量用：接收器在线但鼠标休眠/关机时，读到的是残留帧或设备事件帧。
                    // 真机 2026-10-27：charge=0x70、percent=0 被当成"真 0%"显示 —— 正是这条漏网。
                    string why = DescribeImplausible(status, charge, percent);
                    string raw = HexHead(response, 8);
                    problems.Add($"{link}: 应答不可信（{why}，原始 {raw}）");
                    _log.Warn($"{link} 第 {stale}/{StaleRetries} 次：应答不可信（{why}），原始 {raw}");
                    seen = seen is null ? ReadOutcome.Implausible : ReadOutcomes.Escalate(seen.Value, ReadOutcome.Implausible);

                    // 设备只会复读同一帧 ⇒ 它根本没在应答这条命令（休眠），本轮再重试是白等。
                    // 真机实测：鼠标关掉后 4 分钟里每一帧都是 `BA 01 70 00 …`，一模一样。
                    if (raw == previousRaw)
                    {
                        _log.Debug($"{link}: 连续两次拿到同一帧不可信应答，判定为休眠，本轮不再重试");
                        break;
                    }

                    previousRaw = raw;
                    Thread.Sleep(SendGapMs);
                    continue;
                }

                _workingConn = conn;
                bool charging = charge == 2;
                sample = new BatterySample(
                    Percent: percent,
                    RawValue: (byte)percent,
                    Charging: charging,
                    Timestamp: DateTimeOffset.Now,
                    Scale: BatteryScale.Direct,
                    RawHex: $"conn=0x{conn:X2} resp={HexHead(response, 8)}");

                _log.Debug($"电量: {percent}%（雷柏 {link}），充电={charging}，charge={charge}，原始 {HexHead(response, 8)}");
                error = string.Empty;
                outcome = ReadOutcome.Ok;
                return true;
            }

            if (fatal) break;

            // 这条链路**回过帧**（只是内容不可信）⇒ 设备就挂在它上面，不必再试另一条链路。
            // 无线鼠标不可能同时挂在 2.4G 和有线两端；再试一次只是白等 ~1.2 秒（真机日志实测）。
            if (answeredHere)
            {
                _log.Debug($"{link} 已应答（内容不可信），不再尝试其它链路");
                break;
            }
        }

        error = problems.Count > 0
            ? "雷柏读电量失败（接收器在线但没拿到可信电量，鼠标可能休眠/关机，动一下它再试）：" +
              string.Join("；", problems.Distinct())
            : "雷柏读电量失败：连接成功但没拿到可信的电量应答";
        outcome = seen ?? ReadOutcome.NoResponse;
        _log.Error(error);
        return false;
    }

    // ────────────────────────── DPI（EEPROM 块读改写）──────────────────────────

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;

        if (!TryReadDpiTables(out byte[] xTable, out byte[] yTable, out int activeStage, out string readError))
        {
            error = readError;
            return false;
        }

        int index = activeStage * 2;
        int x = xTable[index] | (xTable[index + 1] << 8);
        int y = yTable[index] | (yTable[index + 1] << 8);

        // Y 表偶尔与 X 表不一致（官方驱动允许分离），这里如实报告
        dpiX = x;
        dpiY = y;

        if (x <= 0 || y <= 0)
        {
            // 别只返回 false：日志里出现过"读 DPI 失败（VT9 Pro）："后面是空的，等于让用户猜
            error = $"当前档位（第 {activeStage + 1} 档）读到的值是 {x}x{y}，不像 DPI（X 表 {LastXTableHex}）";
            _log.Warn($"雷柏 {error}");
            return false;
        }

        error = string.Empty;
        _log.Debug($"雷柏 DPI：档位 {activeStage + 1}/{xTable[14]} = {x}x{y}");
        return true;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        if (!TryReadDpiTables(out byte[] xTable, out byte[] yTable, out int activeStage, out string readError))
        {
            error = "读不到 DPI 表，出于安全已放弃写入：" + readError;
            _log.Error(error);
            return false;
        }

        // 安全闸：只有读回来的块确实像 DPI 表，才允许写（地址若猜错，写了会破坏配置）
        if (!LooksLikeDpiTable(xTable, activeStage, out string gateReason))
        {
            error = $"0x{AddrDpiX:X3} 处的内容不像 DPI 表（{gateReason}，X={LastXTableHex}），" +
                    "已拒绝写入以免破坏鼠标配置";
            _log.Error(error);
            return false;
        }

        if (!LooksLikeDpiTable(yTable, activeStage, out string gateReasonY))
        {
            error = $"0x{AddrDpiY:X3} 处的内容不像 DPI 表（{gateReasonY}，Y={LastYTableHex}），" +
                    "已拒绝写入以免破坏鼠标配置";
            _log.Error(error);
            return false;
        }

        int target = NormalizeDpi(Math.Min(dpiX, dpiY));
        int index = activeStage * 2;
        int currentX = xTable[index] | (xTable[index + 1] << 8);

        if (currentX == target)
        {
            error = string.Empty;
            _log.Info($"雷柏当前档位 DPI 已是 {target}，无需写入");
            return true;
        }

        byte[] originalX = (byte[])xTable.Clone();
        byte[] originalY = (byte[])yTable.Clone();

        WriteStage(xTable, index, target);
        WriteStage(yTable, index, target);

        _log.Info($"雷柏写 DPI：档位 {activeStage + 1} {currentX} → {target}（X/Y 同步）");

        if (!TryWriteBlock(AddrDpiX, xTable, out string writeXError))
        {
            error = $"写 X 表失败：{writeXError}";
            _log.Error(error);
            return false;
        }

        if (!TryWriteBlock(AddrDpiY, yTable, out string writeYError))
        {
            error = $"写 Y 表失败（X 已写入 {target}）：{writeYError}";
            _log.Error(error);
            return false;
        }

        // 读回校验；不一致就尝试写回原值
        if (!TryReadDpiTables(out byte[] xVerify, out byte[] yVerify, out int verifyStage, out string verifyError))
        {
            error = $"写入后读回失败：{verifyError}";
            _log.Error(error);
            return false;
        }

        int verifyX = xVerify[index] | (xVerify[index + 1] << 8);
        int verifyY = yVerify[index] | (yVerify[index + 1] << 8);
        if (verifyX != target || verifyY != target)
        {
            _log.Error($"雷柏 DPI 校验不一致：写入 {target}，读回 {verifyX}x{verifyY}，尝试恢复原值");

            TryWriteBlock(AddrDpiX, originalX, out _);
            TryWriteBlock(AddrDpiY, originalY, out _);

            error = $"写入后读回不一致（{verifyX}x{verifyY}），已尝试恢复原值";
            return false;
        }

        if (verifyStage != activeStage)
        {
            _log.Warn($"档位下标变化：写入前 {activeStage}，读回 {verifyStage}（可能同时在用官方驱动）");
        }

        error = string.Empty;
        _log.Info($"雷柏 DPI 写入并校验成功：{target}（档位 {activeStage + 1}）");
        return true;
    }

    private static void WriteStage(byte[] table, int index, int dpi)
    {
        table[index] = (byte)(dpi & 0xFF);
        table[index + 1] = (byte)((dpi >> 8) & 0xFF);
    }

    /// <summary>
    /// 只验证"写通路"：把刚读到的 X/Y 表**原样写回去**，再读回逐字节比对。
    ///
    /// 为什么需要它：<see cref="TryWriteDpi"/> 在"目标值等于当前值"时会提前返回，
    /// 于是诊断里那句"写入通路可用"其实没写过任何东西。原样写回是零风险的——
    /// 即便地址猜错，写进去的也是刚从同一地址读出来的字节。
    /// </summary>
    public bool TryVerifyWritePath(out string error)
    {
        if (!TryReadDpiTables(out byte[] xTable, out byte[] yTable, out int activeStage, out string readError))
        {
            error = "读不到 DPI 表：" + readError;
            return false;
        }

        if (!LooksLikeDpiTable(xTable, activeStage, out string xReason))
        {
            error = $"X 表不像 DPI 表（{xReason}），不做写入探测";
            return false;
        }

        if (!LooksLikeDpiTable(yTable, activeStage, out string yReason))
        {
            error = $"Y 表不像 DPI 表（{yReason}），不做写入探测";
            return false;
        }

        if (!TryWriteBlock(AddrDpiX, xTable, out string writeXError))
        {
            error = "写 X 表失败：" + writeXError;
            return false;
        }

        if (!TryWriteBlock(AddrDpiY, yTable, out string writeYError))
        {
            error = "写 Y 表失败：" + writeYError;
            return false;
        }

        if (!TryReadDpiTables(out byte[] xBack, out byte[] yBack, out _, out string verifyError))
        {
            error = "写后读回失败：" + verifyError;
            return false;
        }

        if (!xBack.AsSpan().SequenceEqual(xTable) || !yBack.AsSpan().SequenceEqual(yTable))
        {
            error = $"写后读回不一致：X 写 {ToHex(xTable)} 读 {ToHex(xBack)}；" +
                    $"Y 写 {ToHex(yTable)} 读 {ToHex(yBack)}";
            _log.Error(error);
            return false;
        }

        error = string.Empty;
        _log.Info($"雷柏写通路实测通过（原样写回并读回比对一致）：X={ToHex(xTable)}");
        return true;
    }

    /// <summary>DPI 表结构校验（不知道当前档位时的弱校验，仅供外部调用）。</summary>
    public static bool LooksLikeDpiTable(ReadOnlySpan<byte> table, out string reason) =>
        LooksLikeDpiTable(table, null, out reason);

    /// <summary>
    /// DPI 表结构校验。判据的演变（2026-10-26 真机纠正）：
    ///
    /// 原先照 mousectl 的逆向结果，把 <c>[14]</c> 当作"档数字节"并要求它落在 1..7。
    /// 真机上 VT9 Pro 的 <c>[14]</c> **是 0**，而 <c>[0..13]</c> 里的值（当前档 3200）完全正常，
    /// 于是安全闸把一次合法写入误判成"地址猜错"直接拒了 —— 这才是"调整 DPI 失败"的真因。
    ///
    /// 现在的判据不再依赖那个不可靠的字节：
    /// 1. 块长度必须够 16；
    /// 2. 所有非 0 档位必须落在传感器范围内（挡住"读到别的块"）；
    /// 3. 至少有一个非 0 档位；
    /// 4. **当前档位的值必须是合法的 DPI**（这是最硬的一条：当前值是我们从设备上读到的、
    ///    能跟实际使用值对上的那一格）；
    /// 5. <c>[14]</c>/<c>[15]</c> **完全忽略**。2026-10-26 两次真机读数把它彻底否掉：
    ///    第一次 `[14]=0`（7 档都在），第二次 `[14]=2`（7 档照样都在、当前档变成第 3 档）；
    ///    如果它是"档数"，第二次就会出现"下标 2 ≥ 档数 2"这种自相矛盾的状态 ——
    ///    而且这个判断本身又把一次合法写入挡了一次。它可能只是固件给当前档位留的副本，不可依赖。
    /// </summary>
    public static bool LooksLikeDpiTable(ReadOnlySpan<byte> table, int? activeStage, out string reason)
    {
        reason = string.Empty;
        if (table.Length < DpiBlockLength)
        {
            reason = $"长度只有 {table.Length}";
            return false;
        }

        int plausible = 0;
        for (int stage = 0; stage < StageCount; stage++)
        {
            int value = table[stage * 2] | (table[stage * 2 + 1] << 8);
            if (value == 0) continue;
            if (value < MinDpi || value > MaxDpiValue)
            {
                reason = $"第 {stage + 1} 档 = {value}，超出 {MinDpi}..{MaxDpiValue}";
                return false;
            }

            plausible++;
        }

        if (plausible == 0)
        {
            reason = "7 档全是 0";
            return false;
        }

        if (activeStage is int active)
        {
            if (active < 0 || active >= StageCount)
            {
                reason = $"当前档位下标 {active} 超出 0..{StageCount - 1}";
                return false;
            }

            int current = table[active * 2] | (table[active * 2 + 1] << 8);
            if (current < MinDpi || current > MaxDpiValue)
            {
                reason = $"当前档位（第 {active + 1} 档）的值 = {current}，不像 DPI";
                return false;
            }
        }

        return true;
    }

    private bool TryReadDpiTables(out byte[] xTable, out byte[] yTable, out int activeStage, out string error)
    {
        xTable = Array.Empty<byte>();
        yTable = Array.Empty<byte>();
        activeStage = 0;

        if (!TryReadBlock(AddrDpiX, DpiBlockLength, out byte[] x, out string xError))
        {
            error = $"读 0x{AddrDpiX:X3} 失败：{xError}";
            return false;
        }

        if (!TryReadBlock(AddrDpiY, DpiBlockLength, out byte[] y, out string yError))
        {
            error = $"读 0x{AddrDpiY:X3} 失败：{yError}";
            return false;
        }

        if (!TryReadBlock(AddrDpiCur, DpiCurBlockLength, out byte[] cur, out string curError))
        {
            error = $"读 0x{AddrDpiCur:X3}（当前档位）失败：{curError}";
            return false;
        }

        xTable = x;
        yTable = y;
        activeStage = Math.Clamp((int)cur[0], 0, StageCount - 1);
        LastXTableHex = ToHex(x);
        LastYTableHex = ToHex(y);
        LastStageHex = ToHex(cur);
        error = string.Empty;
        return true;
    }

    private static string ToHex(byte[] data) =>
        data.Length == 0 ? string.Empty : Convert.ToHexString(data).Chunk(2)
            .Select(c => new string(c)).Aggregate((a, b) => a + " " + b);

    /// <summary>取前 n 字节的 hex（日志/诊断里记原始应答，方便下次对着解）。</summary>
    private static string HexHead(byte[] frame, int count) =>
        frame.Length == 0
            ? "（空）"
            : string.Join(' ', frame.Take(Math.Min(count, frame.Length)).Select(b => b.ToString("X2")));

    private static bool IsAllZero(ReadOnlySpan<byte> block)
    {
        foreach (byte b in block)
        {
            if (b != 0) return false;
        }

        return true;
    }

    /// <summary>
    /// 读一块 EEPROM。三重校验：ACK 且长度够 → 内容不是全 0 → 交给调用方做结构校验。
    ///
    /// 为什么要"全 0 = 失败"：鼠标休眠/关机时，接收器仍然会 ACK 一个**全 0 的块**。
    /// 以前这种块被当成合法数据，于是 DPI 显示成 0（真机 2026-10-27 的 diag 就是这样：
    /// `档位 1/0 = 0x0`，看着像"DPI 是 0"，其实鼠标根本没应答）。
    /// </summary>
    private bool TryReadBlock(uint address, int length, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = string.Empty;
        string lastProblem = string.Empty;

        for (int attempt = 1; attempt <= BlockReadAttempts; attempt++)
        {
            foreach (byte conn in OrderedConns())
            {
                if (!TryExchange(conn, BuildFrame(conn, CmdReadBlock, (byte)length, address), out byte[] response,
                        out string requestError, out _))
                {
                    lastProblem = requestError;
                    continue;
                }

                if (!TryParseBlockData(response, length, out byte[] block))
                {
                    lastProblem = $"应答不是块数据（{HexHead(response, 8)}）";
                    continue;
                }

                // 全 0 的"Dpi 表"= 鼠标没应答（接收器 ACK 了空块）。
                // 只对 16 字节的 X/Y 表判定：4 字节的"当前档位"块本来就可能是 00 00 00 00（第 1 档）。
                if (length >= 8 && IsAllZero(block))
                {
                    lastProblem = $"0x{address:X3} 读回全 0（鼠标休眠/未与接收器连接，不是真实数据）";
                    _log.Warn($"雷柏块读 0x{address:X3} 第 {attempt}/{BlockReadAttempts} 次：{lastProblem}");
                    continue;
                }

                _workingConn = conn;
                data = block;
                error = string.Empty;
                return true;
            }

            if (attempt < BlockReadAttempts) Thread.Sleep(BlockRetryDelayMs);
        }

        error = lastProblem.Length > 0 ? lastProblem : "没有可用链路";
        return false;
    }

    private bool TryWriteBlock(uint address, byte[] data, out string error)
    {
        error = string.Empty;
        foreach (byte conn in OrderedConns())
        {
            byte[] frame = BuildFrame(conn, CmdWriteBlock, (byte)data.Length, address, data);
            if (!TryExchange(conn, frame, out byte[] response, out string requestError, out _))
            {
                error = requestError;
                continue;
            }

            if (response.Length < 2 || !TryParseResponse(response, out byte status, out _, out _) || status != AckOk)
            {
                error = "写入应答 status 非 0x01";
                continue;
            }

            _workingConn = conn;
            error = string.Empty;
            return true;
        }

        error = error.Length > 0 ? error : "没有可用链路";
        return false;
    }

    private IEnumerable<byte> OrderedConns()
    {
        if (_workingConn is byte known)
        {
            yield return known;
        }

        foreach (byte conn in ConnBytes)
        {
            if (_workingConn is byte used && used == conn) continue;
            yield return conn;
        }
    }

    private static string DescribeConn(byte conn) => conn == 0xA5 ? "2.4G 接收器" : "有线";

    /// <summary>一次完整交换：写帧 → 轮询 input 直到"见过 busy 之后的 OK"，超时重发。</summary>
    private bool TryExchange(byte conn, byte[] frame, out byte[] response, out string error, out ReadOutcome outcome)
    {
        response = Array.Empty<byte>();
        error = string.Empty;
        outcome = ReadOutcome.NoResponse;

        if (frame.Length != _transport.Info.OutputReportLength)
        {
            Array.Resize(ref frame, _transport.Info.OutputReportLength);
        }

        var input = new byte[Math.Max(FrameLength, (int)_transport.Info.InputReportLength)];

        for (int send = 1; send <= MaxSends; send++)
        {
            if (!_transport.TryWriteOutputReport(frame, out int writeError))
            {
                error = $"写 output report 失败，Win32 错误 {writeError}";
                outcome = ReadOutcomes.FromWin32(writeError);
                if (writeError == 5)
                {
                    error += "（拒绝访问：该接口需要读写权限，被系统或官方软件占着）";
                }
                return false;
            }

            long sentAt = Stopwatch.GetTimestamp();
            long deadline = sentAt + ExchangeTimeoutMs * Stopwatch.Frequency / 1000;
            bool busySeen = false;
            bool noBusy = false;
            int readFailures = 0;

            while (Stopwatch.GetTimestamp() < deadline && !noBusy)
            {
                Thread.Sleep(PollGapMs);

                Array.Clear(input);
                input[0] = ReportId;
                if (!_transport.TryGetInputReport(input, out int bytesRead, out int readError))
                {
                    if (++readFailures >= 5)
                    {
                        error = $"读 input report 连续失败 {readFailures} 次，Win32 错误 {readError}";
                        outcome = ReadOutcomes.FromWin32(readError);
                        return false;
                    }

                    _log.Debug($"读 input report 失败 Win32={readError}，继续轮询");
                    continue;
                }

                readFailures = 0;

                if (!TryParseResponse(input.AsSpan(0, Math.Max(bytesRead, 0)), out byte status, out _, out _))
                {
                    continue;
                }

                if (status != AckOk)
                {
                    busySeen = true;
                    continue;
                }

                if (busySeen)
                {
                    response = (byte[])input.Clone();
                    outcome = ReadOutcome.Ok;
                    _log.Debug($"雷柏应答（第 {send} 次发送）：见过 busy→OK 序列，可信度最高，原始 {HexHead(response, 8)}");
                    return true;
                }

                // 收到 OK 但**没见过 busy**：这帧很可能是驱动里残留的"上一条"应答（不是回给这次的）。
                // 丢弃并立刻重发，不要耗掉剩余的 1.5 秒超时。
                // ⚠️ 这里**不返回**这一帧 —— 内容对不上命令的应答一律不用（2026-10-27 的假 0% 就是这么进来的）。
                long elapsedMs = (Stopwatch.GetTimestamp() - sentAt) * 1000 / Stopwatch.Frequency;
                if (elapsedMs >= BusyAcceptMs)
                {
                    _log.Debug($"雷柏第 {send}/{MaxSends} 次发送：{elapsedMs}ms 收到 OK 但没见到 busy，" +
                               $"判为残留应答，重发（原始 {HexHead(input, 8)}）");
                    noBusy = true;
                }
            }

            if (!noBusy)
            {
                _log.Debug($"雷柏第 {send}/{MaxSends} 次发送超时未拿到 busy-OK，重发");
            }

            Thread.Sleep(SendGapMs);
        }

        error = $"连续 {MaxSends} 次都没拿到可信应答（鼠标可能休眠，动一下再试）";
        outcome = ReadOutcome.NoResponse;
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
