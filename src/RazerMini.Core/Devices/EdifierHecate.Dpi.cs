namespace RazerMini.Core.Devices;

/// <summary>
/// 漫步者的**DPI 层**：7 档表定位、换算、读改写与校验。
///
/// 页 <see cref="PageSettings"/> 里自 <see cref="DpiTableOffset"/>（`0x10`）起是 **7 条记录、每条 9 字节**，
/// 第 k 档在 `0x10 + 9k`，记录头 2 字节是该档 DPI 的原始值（u16 小端）。**DPI = (raw + 1) × 50**。
/// 当前档位下标在页内偏移 <see cref="SettingsStageIndexOffset"/>（12），要读/写的是 `0x10 + 9×下标` 那一格。
///
/// 真实对照（2026-10-26 用户提供官方 App 的七档值，逐个吻合）：
/// <code>
/// 档位   1     2     3     4     5      6     7
/// DPI   400   800  1000  1200  1300*  1600  2500    (* 用户手动改过)
/// raw   07    0F    13    17    19     1F    31
/// 偏移  0x10  0x19  0x22  0x2B  0x34   0x3D  0x46
/// </code>
/// </summary>
public sealed partial class EdifierHecate
{
    /// <summary>
    /// 页 <see cref="PageSettings"/> 里 7 个档位记录表的起点：**每条记录 9 字节，第 k 档在
    /// `0x10 + 9*k`**（k 从 0 开始），记录头 2 字节是该档 DPI 的原始值（u16 小端）。
    /// </summary>
    public const int DpiTableOffset = 0x10;

    /// <summary>档位记录长度（字节）。</summary>
    public const int DpiRecordSize = 9;

    /// <summary>档位总数。</summary>
    public const int StageCount = 7;

    /// <summary>DPI 步进：DPI = (raw + 1) × 50。</summary>
    public const int DpiStep = 50;

    public const int MinDpi = 50;
    public const int MaxDpiValue = 26000;

    /// <summary>读设置页并取 DPI 字段的重试次数与间隔（字段为 0 说明设备没进配置会话）。</summary>
    private const int DpiReadAttempts = 3;

    private const int DpiRetryGapMs = 150;

    /// <summary>第 <paramref name="stage"/> 档（0 基）DPI 字段在页内的偏移。</summary>
    public static int DpiOffsetForStage(int stage) => DpiTableOffset + (DpiRecordSize * stage);

    /// <summary>原始值 → DPI。</summary>
    public static int RawToDpi(int raw) => (raw + 1) * DpiStep;

    /// <summary>DPI → 原始值（调用前请先 <see cref="NormalizeDpi"/>）。</summary>
    public static int DpiToRaw(int dpi) => (dpi / DpiStep) - 1;

    /// <summary>最近一次读到的 DPI 原始字段值（诊断用：与官方 App 显示的数对不上时看它）。</summary>
    public int? LastDpiRaw { get; private set; }

    /// <summary>最近一次读到的当前档位下标（0 基）。</summary>
    public int? LastDpiStage { get; private set; }

    /// <summary>把设置页字节解析成"1档=400 2档=800 …"（诊断/对账用）。</summary>
    public static List<string> DescribeStageTable(ReadOnlySpan<byte> page)
    {
        var parts = new List<string>();
        for (int stage = 0; stage < StageCount; stage++)
        {
            int offset = DpiOffsetForStage(stage);
            if (offset + 1 >= page.Length) break;
            int raw = page[offset] | (page[offset + 1] << 8);
            parts.Add($"{stage + 1}档={RawToDpi(raw)}");
        }

        return parts;
    }

    /// <summary>把 DPI 规整到 50..26000、50 步进。</summary>
    public static int NormalizeDpi(int dpi)
    {
        int clamped = Math.Clamp(dpi, MinDpi, MaxDpiValue);
        int stepped = (int)Math.Round((double)clamped / DpiStep) * DpiStep;
        return Math.Clamp(stepped, MinDpi, MaxDpiValue);
    }

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;

        if (!TryReadSettingsForDpi(out _, out int stage, out int raw, out error))
        {
            error = "读 DPI 失败：" + error;
            return false;
        }

        LastDpiRaw = raw;
        LastDpiStage = stage;
        dpiX = RawToDpi(raw);
        dpiY = dpiX;
        error = string.Empty;
        _log.Debug($"漫步者 DPI：档位 {stage + 1}/{StageCount}，页 0x05 +0x{DpiOffsetForStage(stage):X2} " +
                   $"raw={raw} → {dpiX}（(raw+1)×{DpiStep}）");
        return true;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        int target = NormalizeDpi(Math.Min(dpiX, dpiY));

        if (!TryReadSettingsForDpi(out byte[] page, out int stage, out int raw, out error))
        {
            error = "已拒绝写入：" + error;
            _log.Error(error);
            return false;
        }

        int currentDpi = RawToDpi(raw);
        if (currentDpi == target)
        {
            error = string.Empty;
            _log.Info($"漫步者当前档位 DPI 已是 {target}，无需写入");
            return true;
        }

        int offset = DpiOffsetForStage(stage);
        int units = DpiToRaw(target);

        byte[] original = (byte[])page.Clone();
        byte[] updated = (byte[])page.Clone();
        updated[offset] = (byte)(units & 0xFF);
        updated[offset + 1] = (byte)((units >> 8) & 0xFF);

        _log.Info($"漫步者写 DPI：档位 {stage + 1}/{StageCount} {currentDpi} → {target}" +
                  $"（页 0x05 +0x{offset:X2} raw {raw} → {units}，整页 84 字节回写，与官方 App 一致）");
        _log.Debug($"写前页面：{Convert.ToHexString(original)}");

        if (!TryWriteConfigPage(updated, out string writeError))
        {
            error = "写 DPI 失败：" + writeError;
            return false;
        }

        // 读回校验：**按我们写的那个偏移**核对，不按"当前档位"——
        // 写完设备有可能把当前档位挪回第 1 档，按档位读会误判成失败，
        // 然后"恢复原值"就把刚写成功的值又覆盖掉了（2026-10-26 真机踩过）。
        if (!TryReadPage(PageSettings, SettingsPageLength, out byte[] verify, out string verifyError))
        {
            error = "写入后读回失败：" + verifyError;
            _log.Error(error);
            return false;
        }

        _log.Debug($"写后页面：{Convert.ToHexString(verify)}");

        int verifyAtOffset = verify[offset] | (verify[offset + 1] << 8);
        int verifyStage = verify[SettingsStageIndexOffset];

        if (verifyAtOffset != units)
        {
            _log.Error($"漫步者 DPI 写入未生效：偏移 0x{offset:X2} 期望 raw={units}，" +
                       $"读回 raw={verifyAtOffset}（当前档位下标 {verifyStage}），整页写回原值");
            TryWriteConfigPage(original, out _);
            error = $"写入未生效（写入 {target}，该格读回 {RawToDpi(verifyAtOffset)}），已写回原值";
            return false;
        }

        if (verifyStage != stage)
        {
            _log.Warn($"漫步者写入生效，但当前档位下标从 {stage} 变成了 {verifyStage}" +
                      "（设备自己的行为，页面第 1 项记录里就是新值）");
        }

        LastDpiRaw = verifyAtOffset;
        LastDpiStage = verifyStage;
        error = string.Empty;
        _log.Info($"漫步者 DPI 写入并校验成功：页 0x05 +0x{offset:X2} raw={units} → {target}" +
                  $"（写的是第 {stage + 1} 档，当前档位下标 {verifyStage}）");
        return true;
    }

    /// <summary>
    /// 读设置页并取出可信的 DPI 字段，不可信就重读几遍。
    /// 设备不在配置会话里时会回"帧头 + 全 0 数据"，此时字段读出来是 0 —— 绝不能拿它去写。
    /// </summary>
    private bool TryReadSettingsForDpi(out byte[] page, out int stage, out int raw, out string error)
    {
        page = Array.Empty<byte>();
        stage = -1;
        raw = 0;
        error = string.Empty;

        for (int attempt = 1; attempt <= DpiReadAttempts; attempt++)
        {
            if (!TryReadPage(PageSettings, SettingsPageLength, out page, out error))
            {
                // TryReadPage 内部已经重试过；再加一层只会让"设备无响应"时按钮卡十几秒
                _log.Debug($"读设置页失败（内部已重试）：{error}");
                return false;
            }

            if (TryLocateDpiField(page, out stage, out raw, out string reason))
            {
                return true;
            }

            error = $"设置页结构不像预期（{reason}）";
            _log.Debug($"设置页第 {attempt}/{DpiReadAttempts} 次不可信：{reason}");
            _log.Debug($"设置页原始字节（{page.Length}B）：{Convert.ToHexString(page)}");

            if (attempt < DpiReadAttempts)
            {
                Thread.Sleep(DpiRetryGapMs);
            }
        }

        return false;
    }

    /// <summary>
    /// 按"当前档位下标"定位 DPI 记录，并做可信性校验：
    /// ① 页面放得下 7 条记录；② 非零字节数够多（真页有几十个，空页/半空页会被挡下）；
    /// ③ 档位下标 0..6；④ 该档 raw 换算后不超过上限。
    /// </summary>
    private static bool TryLocateDpiField(ReadOnlySpan<byte> page, out int stage, out int raw, out string reason)
    {
        stage = -1;
        raw = 0;
        reason = string.Empty;

        int lastField = DpiOffsetForStage(StageCount - 1) + 1;
        if (page.Length <= lastField)
        {
            reason = $"页长度只有 {page.Length}，放不下 {StageCount} 档记录（需要 {lastField + 1} 字节）";
            return false;
        }

        int nonZero = 0;
        foreach (byte b in page)
        {
            if (b != 0) nonZero++;
        }

        if (nonZero < 8)
        {
            reason = $"整页只有 {nonZero} 个非零字节，不像真实配置页";
            return false;
        }

        int index = page[SettingsStageIndexOffset];
        if (index >= StageCount)
        {
            reason = $"当前档位下标 {index} 超出 0..{StageCount - 1}";
            return false;
        }

        int offset = DpiOffsetForStage(index);
        int value = page[offset] | (page[offset + 1] << 8);
        if (value > DpiToRaw(MaxDpiValue))
        {
            reason = $"第 {index + 1} 档 raw={value} 换算后是 {RawToDpi(value)}，超出 {MinDpi}..{MaxDpiValue}";
            return false;
        }

        stage = index;
        raw = value;
        return true;
    }

    /// <summary>
    /// 写**整页** 0x05（`[3]=0x06`，按 24 字节分块），外面用 `0x01` / `0x02` 夹住 —— 与官方 App 完全一致。
    ///
    /// 为什么整页写：App 改 DPI 时是四块一起写（抓包 t=13973~14001）。只写一块虽然有时也生效
    /// （真机实测 1600→400 成功），但设备那一侧很可能是"分块暂存、整页应用"，缺块时行为不可预测
    /// （同一段代码紧接着 400→1600 就没生效）。
    /// </summary>
    private bool TryWriteConfigPage(ReadOnlySpan<byte> page, out string error)
    {
        error = string.Empty;

        if (page.Length < PageChunk)
        {
            error = $"页面太短（{page.Length} 字节）";
            return false;
        }

        TryBeginConfigBurst();

        int offset = 0;
        while (offset < page.Length)
        {
            int chunk = Math.Min(PageChunk, page.Length - offset);
            if (!ExchangeRaw(CmdWritePage, (byte)chunk, (byte)offset, page.Slice(offset, chunk),
                    ExchangeTimeoutMs, out _, out string chunkError, out _))
            {
                error = $"写页 0x{PageSettings:X2} 偏移 0x{offset:X2} 失败：{chunkError}";
                TryEndConfigBurst();
                return false;
            }

            offset += chunk;
        }

        TryEndConfigBurst();
        return true;
    }
}
