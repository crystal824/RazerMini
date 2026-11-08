namespace RazerMini.Core.Devices;

/// <summary>XInput 报的电池类型（对应 <c>XINPUT_BATTERY_INFORMATION.BatteryType</c>）。</summary>
public enum XboxBatteryType : byte
{
    Disconnected = 0x00,
    Wired = 0x01,
    Alkaline = 0x02,
    NiMh = 0x03,
    Unknown = 0xFF,
}

/// <summary>XInput 报的电池档位（只有 4 档，对应 <c>BATTERY_LEVEL_*</c>）。</summary>
public enum XboxBatteryLevel : byte
{
    Empty = 0x00,
    Low = 0x01,
    Medium = 0x02,
    Full = 0x03,
}

/// <summary>
/// Xbox 手柄电量的**换算与文案**（不含任何 P/Invoke，Linux 上可断言）。
///
/// 结论先写清（2026-10-27 核实）：Xbox 手柄在 Windows 上唯一的用户态电量来源是
/// <c>XInputGetBatteryInformation</c>，**只有 4 档**（空/低/中/满），拿不到百分比。
/// 参考实现 `NiyaShy/XB1ControllerBatteryIndicator` 也是这么做的，且明确说明：
/// 只对**接收器（微软私有协议，官方/第三方都行）**有效，蓝牙连接读不到或读数错误。
/// 走蓝牙就没这问题——蓝牙那条路微软自己的实现也不给电量。
///
/// 想要精确百分比只能绕过 <c>xboxgip.sys</c> 直接说 GIP，那就得替换驱动（会破坏游戏里的正常使用），
/// 所以本工具**不做**这件事（详见 README §8.3）。
/// </summary>
public static class XboxBattery
{
    /// <summary>4 档 → 百分比。桶的中值，够图标和"要不要换电池"判断用。</summary>
    public static int ToPercent(XboxBatteryLevel level) => level switch
    {
        XboxBatteryLevel.Empty => 5,
        XboxBatteryLevel.Low => 25,
        XboxBatteryLevel.Medium => 60,
        XboxBatteryLevel.Full => 100,
        _ => 0,
    };

    /// <summary>
    /// 这个电池类型有没有"电量"可读。
    /// <see cref="XboxBatteryType.Wired"/> 表示有线供电（没有电池），不该出现在托盘里。
    /// </summary>
    public static bool HasBattery(XboxBatteryType type) =>
        type is XboxBatteryType.Alkaline or XboxBatteryType.NiMh or XboxBatteryType.Unknown;

    public static string DescribeType(XboxBatteryType type) => type switch
    {
        XboxBatteryType.Disconnected => "未连接",
        XboxBatteryType.Wired => "有线供电（无电池）",
        XboxBatteryType.Alkaline => "碱性电池（AA）",
        XboxBatteryType.NiMh => "镍氢电池组（充电包）",
        XboxBatteryType.Unknown => "电池类型未知",
        _ => $"未知类型 0x{(byte)type:X2}",
    };

    public static string DescribeLevel(XboxBatteryLevel level) => level switch
    {
        XboxBatteryLevel.Empty => "空",
        XboxBatteryLevel.Low => "低",
        XboxBatteryLevel.Medium => "中",
        XboxBatteryLevel.Full => "满",
        _ => $"未知档位 0x{(byte)level:X2}",
    };
}
