namespace RazerMini.Core.Hid;

/// <summary>
/// 一个 HID 接口（top-level collection）的静态描述。全部来自 Windows HID API 的实测值，
/// 不含任何协议语义 —— 协议语义在 Devices 层。
/// </summary>
public sealed class HidDeviceInfo
{
    /// <summary>设备路径，CreateFile 用。</summary>
    public string Path { get; init; } = string.Empty;

    public ushort VendorId { get; init; }
    public ushort ProductId { get; init; }

    /// <summary>HIDP_CAPS.UsagePage</summary>
    public ushort UsagePage { get; init; }

    /// <summary>HIDP_CAPS.Usage</summary>
    public ushort Usage { get; init; }

    public ushort InputReportLength { get; init; }
    public ushort OutputReportLength { get; init; }

    /// <summary>feature report 总长度（含 report ID），雷蛇协议要求 &gt;= 91。</summary>
    public ushort FeatureReportLength { get; init; }

    /// <summary>设备管理器里的产品串，可能为空。</summary>
    public string ProductName { get; init; } = string.Empty;

    /// <summary>
    /// 是否成功打开过。**打不开的接口也要出现在列表里** ——
    /// 否则"为什么找不到 91 字节通道"就成了需要猜测的谜（2026-10-26 真机踩过）。
    /// </summary>
    public bool Opened { get; init; } = true;

    /// <summary>三种访问模式都失败时的 Win32 错误码。</summary>
    public int OpenError { get; init; }

    /// <summary>成功打开时用的访问模式（读写 / 只读 / 仅查询）。</summary>
    public string OpenMode { get; init; } = string.Empty;

    /// <summary>设备路径里的 MI_xx（复合设备才带）。</summary>
    public string InterfaceSuffix
    {
        get
        {
            int i = Path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? string.Empty : Path.Substring(i, Math.Min(6, Path.Length - i));
        }
    }

    public override string ToString() =>
        $"{VendorId:X4}:{ProductId:X4} usage={UsagePage:X4}/{Usage:X4} " +
        $"feat={FeatureReportLength} in={InputReportLength} out={OutputReportLength}" +
        (string.IsNullOrEmpty(ProductName) ? string.Empty : $" \"{ProductName}\"") +
        (Opened ? string.Empty : $" [打不开 Win32={OpenError}]");
}
