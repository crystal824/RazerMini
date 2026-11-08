namespace RazerMini.Core.Devices;

/// <summary>
/// 已知雷蛇鼠标型号表。事务号（transaction id）必须按型号查表 —— 抄错会一条应答都收不到，
/// 现象酷似"设备不支持"（HANDOFF 第七节第 1 条，技能里也记着）。
/// </summary>
public sealed record RazerMouseModel(
    ushort VendorId,
    ushort ProductId,
    string Name,
    byte TransactionId,
    int MaxDpi,
    bool SupportsBattery,
    bool SupportsDpi,
    bool IsWireless,
    int Preference = 0)
{
    public string Id => $"{VendorId:X4}:{ProductId:X4}";
}

public static class RazerCatalog
{
    public const ushort RazerVendorId = 0x1532;

    /// <summary>雷蛇协议通道的 feature report 总长度（含 report ID）。</summary>
    public const int ProtocolReportLength = 91;

    /// <summary>
    /// 已知型号表。Preference 越小越优先试：
    /// 007B（Viper Ultimate 接收器）先试，007E（Mouse Dock）最后 ——
    /// openrazer 的 charge_level 分表里**没有 007E**，真机实测它对电量命令回 `0x05 不支持`；
    /// 007E 只有底座自己的鼠标集合同样暴露 91 字节通道，容易被误选（2026-10-26 真机踩过）。
    /// </summary>
    public static readonly IReadOnlyList<RazerMouseModel> Known = new[]
    {
        new RazerMouseModel(0x1532, 0x007B, "Razer Viper Ultimate（无线接收器）", 0xFF, 20000, true, true, true, Preference: 0),
        new RazerMouseModel(0x1532, 0x007A, "Razer Viper Ultimate（有线）", 0xFF, 20000, true, true, false, Preference: 1),
        new RazerMouseModel(0x1532, 0x007E, "Razer Mouse Dock（充电底座）", 0xFF, 20000, true, true, true, Preference: 9),
    };

    public static RazerMouseModel? Find(ushort vendorId, ushort productId)
    {
        foreach (var m in Known)
        {
            if (m.VendorId == vendorId && m.ProductId == productId) return m;
        }
        return null;
    }

    /// <summary>只按 PID 匹配也算命中（无线经底座时 PIDs 会互换，见 HANDOFF 第七节第 2 条）。</summary>
    public static bool IsKnownRazerPid(ushort vendorId, ushort productId) =>
        vendorId == RazerVendorId && Find(vendorId, productId) is not null;
}
