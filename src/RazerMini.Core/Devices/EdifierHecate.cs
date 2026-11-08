using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

/// <summary>
/// 漫步者 HECATE G3M Pro（`320F:706E`，Telink 方案）。**只读电量 + DPI 读写**。
///
/// 判据（三者同时满足）：VID:PID + `usage=0xFF1C/0x0092` + `in=64/out=64`。
///
/// **2026-10-26 用 USBPcap 抓官方 HECATE Connect 流量后改写**：这台设备**能主动查询**。
/// 之前"只被动推状态帧"的结论只说明它自己也会推，但被动采样在诊断里经常十次都碰不到一条，
/// 主动一问一答才是可靠路径。
///
/// 报文（64 字节，report ID `0x04`，中断 OUT 0x05 / IN 0x82，严格一问一答）：
/// <code>
///   绝对下标 : [0]   [1]    [2]    [3]     [4]     [5]     [6] [7]   [8..]数据
///   含义     : 0x04  校验和 0x00   命令    长度    偏移    0   0
///   载荷相对 :  p     p+0    p+1    p+2     p+3     p+4     p+5 p+6   p+7..
/// </code>
/// 其中 `p` = 载荷起点（报文带 report ID 时 p=1，驱动把 ID 摘掉时 p=0）——两种都兼容。
/// 设备把请求原样回显（校验和/命令/长度/偏移全回），数据接在载荷第 7 字节之后。
///
/// ⚠️ **`[1]` 是校验和，不是序号**：`[1] = (字节[3]..字节[63] 之和) &amp; 0xFF`。
/// 这是 2026-10-26 用抓包里 20 多条帧逐条验算出来的（`03+18=1b`、`1a+06=20`、
/// 整条 24 字节配置块 `79 02 06 18 …` 的校验正好 `0x79`）。**发错的校验和会被设备静默丢弃**，
/// 表现就是"写成功、一条应答都收不到"。回复里设备原样回显这个字节（不会按新数据重算），
/// 所以拿它跟请求比对即可确认"这条应答是回给我的"。
///
/// | 命令 | 含义 | 说明 |
/// |---|---|---|
/// | `[3]=0x1A` `[4]=0x06` | 查电量 | 回 `[8]`=百分比、`[9]`=充电标志（本机抓包 `5B`=91% 放电中） |
/// | `[3]=页号` | 读配置页 | 页 `0x03`(34B)/`0x05`(84B)/`0x07`(126B)，按 24 字节分块读 |
/// | `[3]=0x28` `[4]=0x01` | **设**当前档位 | 数据字节 = 目标下标。**不是读**，本类绝不主动发它 |
/// | `[3]=0x06` | 写配置页 | 同读取带 `[4]=长度 [5]=偏移`，数据从 `[8]` 起 |
///
/// **DPI 在页 `0x05` 自 `0x10` 起的 7 条记录里**（每条 9 字节，第 k 档在 `0x10 + 9k`，
/// 记录头 u16 小端 = raw，`DPI = (raw+1)×50`），当前档位下标在页内偏移 12。
/// 详见 <c>EdifierHecate.Dpi.cs</c> 与技能 `user-peripheral-protocols`。
///
/// 实现分四个 partial 文件：
/// <list type="bullet">
/// <item>本文件：常量 / 属性 / 电量（主动 + 被动兜底）/ 生命周期</item>
/// <item><c>.Frames.cs</c>：帧构造、校验和、交换、会话初始化</item>
/// <item><c>.Pages.cs</c>：配置页按 24 字节分块读</item>
/// <item><c>.Dpi.cs</c>：7 档表解析、DPI 读写与校验</item>
/// </list>
/// </summary>
public sealed partial class EdifierHecate : IBatteryDevice
{
    public const ushort VendorId = 0x320F;
    public const ushort ProductId = 0x706E;
    public const ushort VendorUsagePage = 0xFF1C;
    public const ushort VendorUsage = 0x0092;
    public const byte ReportId = 0x04;

    /// <summary>被动状态帧第二字节（心跳是 0xAA）。</summary>
    public const byte StatusLead = 0x20;

    /// <summary>被动状态帧第四字节（payload 长度）。</summary>
    public const byte StatusLenByte = 0x1A;

    // ── 主动协议（抓包确认）──
    public const byte CmdBattery = 0x1A;
    public const byte CmdHeartbeat = 0xAA;
    public const byte BatterySub = 0x06;

    /// <summary>
    /// `0x28` = **设**当前档位（数据字节 = 目标下标），不是查询。
    /// 抓包对照：App 每次发 `04 xx 00 28 01 00 00 00 &lt;n&gt;` 之后就去写页 0x05 偏移 12 = n。
    /// 千万不要拿它当"读档位"用 —— 数据填 0 就等于把鼠标切回第 1 档。本库不主动发它。
    /// </summary>
    public const byte CmdSetStage = 0x28;

    public const byte CmdWritePage = 0x06;
    public const byte PageDevice = 0x03;
    public const byte PageSettings = 0x05;
    public const byte PageProfile = 0x07;

    /// <summary>校验和覆盖的起始下标（`[0]`=report ID、`[1]`=校验和本身、`[2]`=flags 不参与）。</summary>
    private const int ChecksumFrom = 3;

    /// <summary>含 report ID 的 64 字节报文里，数据从第 8 字节开始。</summary>
    public const int PayloadIndex = 8;

    /// <summary>载荷相对偏移：数据起点（p+7 等价于绝对下标 8）。</summary>
    private const int DataOffset = PayloadIndex - 1;

    /// <summary>读配置页的分块大小（官方 App 就是 24 字节一块）。</summary>
    public const int PageChunk = 24;

    /// <summary>
    /// 页 0x03 的长度：官方 App 读了两块 —— `0x18` 偏移 0 加 `0x0A` 偏移 0x18，共 34 字节。
    /// 只读 24 会漏掉尾部 10 字节（一开始就是这么漏的）。
    /// </summary>
    public const int DevicePageLength = 34;
    public const int SettingsPageLength = 84;
    public const int ProfilePageLength = 126;

    /// <summary>
    /// 页 <see cref="PageSettings"/> 里记录"当前档位下标"的偏移。
    /// 官方 App 切档时写的就是这一格（抓包对照：`0x28` 设 5 → 页 0x05 偏移 12 = 05）。
    /// </summary>
    public const int SettingsStageIndexOffset = 12;

    /// <summary>
    /// 官方 App 每次打开设备都会先跑这一段（**逐帧照抄 2026-10-26 的 USBPcap**）。
    ///
    /// 真机实测：不发这段时设备**只回 `0xAA`**，其它命令一条都不理（帧字节和抓包逐字节相同、
    /// 校验和也对，所以不是帧的问题，是设备在等这段会话初始化）。`0x01` / `0x02` 是
    /// "进入配置会话 / 结束"的括号，中间那串是把它关心的三个配置页整页读一遍。
    /// </summary>
    public static readonly (byte Command, byte Length, byte Offset)[] SessionSequence =
    {
        (CmdHeartbeat, 0x00, 0x00),   // 04 AA 00 AA
        (0x01, 0x00, 0x00),           // 04 01 00 01   进入会话
        (PageDevice, 0x18, 0x00),     // 04 1B 00 03 18
        (PageDevice, 0x0A, 0x18),     // 04 25 00 03 0A 18
        (PageProfile, 0x18, 0x00),    // 04 1F 00 07 18
        (PageProfile, 0x18, 0x18),    // 04 37 00 07 18 18
        (PageProfile, 0x18, 0x30),    // 04 4F 00 07 18 30
        (PageProfile, 0x18, 0x48),    // 04 67 00 07 18 48
        (PageProfile, 0x18, 0x60),    // 04 7F 00 07 18 60
        (PageProfile, 0x06, 0x78),    // 04 85 00 07 06 78
        (0x02, 0x00, 0x00),           // 04 02 00 02   结束会话
        (PageSettings, 0x01, 0x00),   // 04 06 00 05 01
        (PageSettings, 0x18, 0x00),   // 04 1D 00 05 18
        (PageSettings, 0x18, 0x18),   // 04 35 00 05 18 18
        (PageSettings, 0x18, 0x30),   // 04 4D 00 05 18 30
        (PageSettings, 0x0C, 0x48),   // 04 59 00 05 0C 48
    };

    public const string ModelName = "HECATE G3M Pro";

    private const int ReportLength = 64;
    private const int MaxReadAttempts = 10;
    private const int ReadGapMs = 20;
    private const int ExchangeTimeoutMs = 400;
    private const int SessionFrameTimeoutMs = 120;
    private const int SessionMaxPasses = 4;
    private const int CmdOnlyFallbackMs = 150;
    private const int PollGapMs = 2;
    private const int Sends = 3;

    private readonly IHidTransport _transport;
    private readonly IInterruptReader? _interruptReader;
    private readonly ILog _log;
    private byte _lastEcho;
    private bool _sessionReady;
    private bool _disposed;

    public EdifierHecate(IHidTransport transport, ILog? log = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _interruptReader = transport as IInterruptReader;
        _log = log ?? NullLog.Instance;
        _log.Debug($"打开漫步者设备: {ModelName}，通道 {transport.Info.Path}");
        _log.Debug($"  通道参数: usage={transport.Info.UsagePage:X4}/{transport.Info.Usage:X4} " +
                  $"in={transport.Info.InputReportLength} out={transport.Info.OutputReportLength} " +
                  $"可写={transport.CanWrite} 读方式={ReadMethod}");
    }

    public string DisplayName => ModelName;
    public string DeviceId => $"{VendorId:X4}:{ProductId:X4}";
    public int MaxDpi => MaxDpiValue;

    /// <summary>DPI 读写已实现（字段位置由官方 App 的写帧直接给出）。</summary>
    public bool SupportsDpi => true;

    public bool SupportsBattery => true;
    public bool CanWrite => _transport.CanWrite;

    /// <summary>报文怎么读来的（诊断用）。HidD_GetInputReport 取不到报文的设备必须走 interrupt 读。</summary>
    public string ReadMethod =>
        _interruptReader is null ? "HidD_GetInputReport（取最近一条）" : "interrupt ReadFile（overlapped）";

    /// <summary>接口判据：VID:PID + 厂商 usage + 64/64 长度，三者同时满足。</summary>
    public static bool MatchesInterface(HidDeviceInfo info) =>
        info.VendorId == VendorId &&
        info.ProductId == ProductId &&
        info.UsagePage == VendorUsagePage &&
        info.Usage == VendorUsage &&
        info.InputReportLength >= ReportLength &&
        info.OutputReportLength >= ReportLength;

    // ────────────────────────── 电量 ──────────────────────────

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        outcome = ReadOutcome.NoResponse;

        if (TryReadBatteryActive(out sample, out string activeError, out ReadOutcome activeOutcome))
        {
            error = string.Empty;
            outcome = ReadOutcome.Ok;
            return true;
        }

        // 句柄已死就没必要再读被动了（被动读也要用同一个句柄）
        if (activeOutcome == ReadOutcome.HandleDead)
        {
            error = $"主动查询失败：{activeError}";
            outcome = activeOutcome;
            _log.Warn(error);
            return false;
        }

        _log.Debug($"漫步者主动查电量没成功（{activeError}），改用被动状态帧兜底");
        if (TryReadBatteryPassive(out sample, out string passiveError))
        {
            error = string.Empty;
            outcome = ReadOutcome.Ok;
            return true;
        }

        // 主动"没有应答/内容不可信" + 被动也读不到 ⇒ 设备在睡；否则归为失败
        outcome = ReadOutcomes.Escalate(activeOutcome, ReadOutcome.NoResponse);
        error = $"主动查询失败：{activeError}；被动帧也失败：{passiveError}";
        _log.Warn(error);
        return false;
    }

    private bool TryReadBatteryActive(out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;

        if (!TryExchange(CmdBattery, BatterySub, 0x00, ReadOnlySpan<byte>.Empty, out byte[] reply, out error,
                out outcome))
        {
            return false;
        }

        if (!TryParseBatteryReply(reply, _lastEcho, out int percent, out bool charging))
        {
            error = "应答不像电量帧";
            outcome = ReadOutcome.Implausible;
            return false;
        }

        sample = new BatterySample(
            Percent: percent,
            RawValue: (byte)Math.Clamp(percent, 0, 255),
            Charging: charging,
            Timestamp: DateTimeOffset.Now,
            Scale: BatteryScale.Direct,
            RawHex: "主动查询 " + Convert.ToHexString(reply.AsSpan(0, PayloadIndex + 2)));

        _log.Debug($"电量: {percent}%（漫步者主动查询），充电={charging}");
        error = string.Empty;
        outcome = ReadOutcome.Ok;
        return true;
    }

    private bool TryReadBatteryPassive(out BatterySample sample, out string error)
    {
        sample = default;
        error = string.Empty;

        var buffer = new byte[Math.Max(ReportLength, (int)_transport.Info.InputReportLength)];
        for (int attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            // 用统一的读路径：有 interrupt 读就用它（HidD_GetInputReport 在这台设备上恒回 31）
            if (!TryNextReport(buffer, 60, out int bytesRead, out int win32Error))
            {
                error = $"读 input report 失败，Win32 错误 {win32Error}（{DescribeWin32(win32Error)}）";
                if (attempt == MaxReadAttempts)
                {
                    _log.Debug($"{error}（{attempt}/{MaxReadAttempts} 次都没读到）");
                }

                Thread.Sleep(ReadGapMs);
                continue;
            }

            if (TryParseStatusFrame(buffer.AsSpan(0, Math.Max(bytesRead, 0)), out int percent, out bool charging))
            {
                sample = new BatterySample(
                    Percent: percent,
                    RawValue: (byte)Math.Clamp(percent, 0, 255),
                    Charging: charging,
                    Timestamp: DateTimeOffset.Now,
                    Scale: BatteryScale.Direct,
                    RawHex: Convert.ToHexString(buffer.AsSpan(0, Math.Min(12, buffer.Length))));

                _log.Debug($"电量: {percent}%（漫步者被动状态帧），充电={charging}");
                error = string.Empty;
                return true;
            }

            Thread.Sleep(ReadGapMs);
        }

        error = $"连续 {MaxReadAttempts} 次没读到状态帧（鼠标可能休眠）";
        return false;
    }

    private static string ExplainReadError(int code) => code switch
    {
        0 => string.Empty,
        31 => " 设备故障：该通道不提供『最后一条报告』缓存，需要 interrupt 读",
        1460 => " 超时：设备没回报文",
        5 => " 拒绝访问",
        6 => " 句柄失效（设备已拔出）",
        1167 => " 设备未连接",
        _ => string.Empty,
    };

    private static string DescribeWin32(int code) => code switch
    {
        0 => "无错误",
        5 => "拒绝访问（被独占或权限不足）",
        6 => "无效句柄（设备已拔出）",
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
