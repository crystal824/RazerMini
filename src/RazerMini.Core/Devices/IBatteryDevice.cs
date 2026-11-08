namespace RazerMini.Core.Devices;

/// <summary>电量原始值的换算口径。</summary>
public enum BatteryScale
{
    /// <summary>raw 0-255 → ×100/255（openrazer 的口径，技能里标注"未在真机确认"，默认用它）。</summary>
    Raw255 = 0,

    /// <summary>raw 直接就是百分比（个别型号/固件）。</summary>
    Direct = 1,
}

/// <summary>一次电量读取的结果。RawValue 永远保留原始字节，方便对账。</summary>
public readonly record struct BatterySample(
    int Percent,
    byte RawValue,
    bool? Charging,
    DateTimeOffset Timestamp,
    BatteryScale Scale,
    string RawHex)
{
    /// <summary>充满判定：电量 100 且明确在充电。</summary>
    public bool IsFull => Percent >= 100 && Charging == true;

    /// <summary>
    /// 设备的"档位文字"（如 <c>满 5/5 档</c>）。**档位制设备**（Switch Pro 5 档、Xbox 4 档）
    /// 没有精确百分比，<see cref="Percent"/> 只是档位折算值；带上它才不会把 `100%` 误读成电表读数
    /// （2026-11-02 用户质疑"玩了一晚上还是 100%"，根因就是档位制被显示成精确值）。
    /// </summary>
    public string? LevelText { get; init; }

    /// <summary>
    /// 档位制设备的档位序号（0 基）与总档数。
    /// 给**渲染层**用：桌面浮窗的等宽字体没有中文字形（Consolas 缺 CJK，GDI+ 不做字体回退，
    /// 会画成方块），所以浮窗只能显示 <c>STAGE 4/5</c> 这类纯 ASCII 文本。
    /// </summary>
    public int? StageLevel { get; init; }

    /// <summary>总档数（Switch Pro 5、Xbox 4）；非档位制设备为 null。</summary>
    public int? StageCount { get; init; }

    public string Describe()
    {
        string charge = Charging switch
        {
            true when Percent >= 100 => " · 已充满",
            true => " · 充电中",
            false => " · 放电中",
            null => string.Empty,
        };
        string head = LevelText is null ? $"{Percent}%" : $"{Percent}% · {LevelText}";
        return $"{head}{charge}";
    }
}

/// <summary>
/// 托盘小工具需要的一台设备：读电量、读/写 DPI。
/// 后续加雷柏/漫步者/高斯时实现这个接口即可，托盘层不用改。
/// </summary>
public interface IBatteryDevice : IDisposable
{
    string DisplayName { get; }
    string DeviceId { get; }
    int MaxDpi { get; }
    bool SupportsDpi { get; }
    bool SupportsBattery { get; }

    /// <summary>
    /// 句柄是否可写（能发 SET_FEATURE / output report）。只读设备用来给 UI 提示。
    /// </summary>
    bool CanWrite { get; }

    /// <summary>
    /// 读电量。除了成功/失败，还要给出 <paramref name="outcome"/> ——
    /// 调用方靠它决定"换候选接口 / 换访问方式 / 剔除设备 / 保留等它醒过来"，
    /// **不要从 <paramref name="error"/> 文案里猜**（见 <see cref="ReadOutcome"/>）。
    /// </summary>
    bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome);

    bool TryReadDpi(out int dpiX, out int dpiY, out string error);
    bool TryWriteDpi(int dpiX, int dpiY, out string error);
}
