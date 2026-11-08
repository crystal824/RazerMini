using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Core.Devices;

public sealed record DeviceSelection(HidDeviceInfo? Interface, RazerMouseModel? Model, string Reason)
{
    public bool Found => Interface is not null && Model is not null;
}

/// <summary>
/// 从枚举到的 HID 接口里挑出雷蛇协议通道。
/// 这段逻辑放在 Core 里是为了能在 Linux 上用假数据断言（真机只能靠用户 Windows 跑）。
/// </summary>
public static class DeviceSelector
{
    /// <summary>
    /// 把可用的 91 字节通道按"值得先试"的顺序排好：型号优先级（007B 先于 007E）→ usage 是否标准。
    /// 调用方应逐个试，谁应答算谁 —— 同一个 1532 VID 下可能同时插着接收器和底座。
    /// </summary>
    public static List<(HidDeviceInfo Info, RazerMouseModel Model)> RankCandidates(
        IReadOnlyList<HidDeviceInfo> interfaces, ILog? log = null)
    {
        log ??= NullLog.Instance;
        var candidates = new List<(HidDeviceInfo Info, RazerMouseModel Model, int UsageRank)>();

        foreach (HidDeviceInfo info in interfaces)
        {
            RazerMouseModel? model = RazerCatalog.Find(info.VendorId, info.ProductId);
            if (model is null) continue;
            if (info.FeatureReportLength < RazerCatalog.ProtocolReportLength) continue;

            bool standardUsage = info.UsagePage == 0x0001 && info.Usage is 0x0002 or 0x0001;
            candidates.Add((info, model, standardUsage ? 0 : 1));
        }

        List<(HidDeviceInfo Info, RazerMouseModel Model)> ranked = candidates
            .OrderBy(c => c.Model.Preference)
            .ThenBy(c => c.UsageRank)
            .Select(c => (c.Info, c.Model))
            .ToList();

        log.Info($"候选协议通道 {ranked.Count} 个：" +
                 string.Join("，", ranked.Select(c => $"{c.Model.Id}({c.Model.Name}) feat={c.Info.FeatureReportLength}")));
        return ranked;
    }

    public static DeviceSelection Select(IReadOnlyList<HidDeviceInfo> interfaces, ILog? log = null)
    {
        log ??= NullLog.Instance;

        if (interfaces.Count == 0)
        {
            const string none = "没有枚举到任何 HID 接口（USB 层问题或权限问题）";
            log.Warn(none);
            return new DeviceSelection(null, null, none);
        }

        List<(HidDeviceInfo Info, RazerMouseModel Model)> ranked = RankCandidates(interfaces, log);
        if (ranked.Count > 0)
        {
            (HidDeviceInfo info, RazerMouseModel model) = ranked[0];
            log.Info($"首选协议通道: {info}");
            return new DeviceSelection(info, model, "91 字节通道 + 型号优先级");
        }

        // 走到这里：要么没有雷蛇 PID，要么雷蛇接口都不到 91 字节
        var razer = interfaces.Where(i => RazerCatalog.Find(i.VendorId, i.ProductId) is not null).ToList();
        if (razer.Count == 0)
        {
            string seen = string.Join(", ", interfaces
                .Select(i => $"{i.VendorId:X4}:{i.ProductId:X4}")
                .Distinct()
                .Take(8));
            string reason = $"没有匹配的雷蛇型号（已找到的 VID:PID：{seen}）";
            log.Warn(reason);
            return new DeviceSelection(null, null, reason);
        }

        string detail = string.Join("; ", razer.Select(i => i.ToString()));
        string fail = $"找到雷蛇设备但没有 91 字节 feature 通道（可能被官方软件占用或只枚举了其它接口）：{detail}";
        log.Warn(fail);
        return new DeviceSelection(null, null, fail);
    }

    public static bool IsKnownRazerPid(ushort vendorId, ushort productId) =>
        RazerCatalog.Find(vendorId, productId) is not null;
}
