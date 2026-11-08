using RazerMini.Core.Hid;

namespace RazerMini.Core.Devices;

/// <summary>支持的设备族。加新品牌时在这里加一项，托盘与诊断会自动带上。</summary>
public enum DeviceFamily
{
    Razer = 0,
    Rapoo = 1,
    Edifier = 2,
    Ganss = 3,
    Nintendo = 4,
    Xbox = 5,
}

/// <summary>一个"值得试一次"的设备候选：某个 HID 接口 + 它属于哪个族 + 型号名 + 顺序。</summary>
public sealed record DeviceCandidate(
    DeviceFamily Family,
    HidDeviceInfo Info,
    string ModelName,
    int Preference,
    string? IdOverride = null)
{
    public string FamilyName => Family switch
    {
        DeviceFamily.Razer => "雷蛇",
        DeviceFamily.Rapoo => "雷柏",
        DeviceFamily.Edifier => "漫步者",
        DeviceFamily.Ganss => "高斯",
        DeviceFamily.Nintendo => "任天堂",
        DeviceFamily.Xbox => "Xbox",
        _ => Family.ToString(),
    };

    /// <summary>非 HID 设备（XInput 手柄）没有 VID:PID 可报，用 <see cref="IdOverride"/> 覆盖。</summary>
    public string Id => IdOverride ?? $"{Info.VendorId:X4}:{Info.ProductId:X4}";

    public string Describe() => $"{FamilyName} {ModelName} [{Id}] {Info.UsagePage:X4}/{Info.Usage:X4} " +
                               $"in={Info.InputReportLength} out={Info.OutputReportLength} feat={Info.FeatureReportLength}";
}

/// <summary>
/// 三个品牌族的接口判据 + 排序。放在 Core 里是为了能在 Linux 上用假接口数据断言（真机只能靠用户 Windows）。
/// 判据必须"VID:PID + usage + 长度"三者同时满足，光看长度或 usage 会误认（技能里的老坑）。
/// </summary>
public static class DeviceCatalog
{
    /// <summary>
    /// 支持的 VID 白名单 —— 枚举时按它预过滤（不开几百个无关句柄）。
    /// **加新品牌时只改这里**，托盘和诊断都引这一份，避免"设备类写好了但枚举根本没带它"。
    /// </summary>
    public static readonly ushort[] SupportedVendorIds =
    {
        0x1532,   // 雷蛇
        0x24AE,   // 雷柏
        0x320F,   // 漫步者
        0x05AC,   // 高斯 / HELLO GANSS（键盘）
        0x057E,   // 任天堂（Switch Pro 手柄，蓝牙 HID）
    };

    public static List<DeviceCandidate> RankCandidates(IReadOnlyList<HidDeviceInfo> interfaces, Logging.ILog? log = null)
    {
        log ??= Logging.NullLog.Instance;
        var candidates = new List<DeviceCandidate>();

        foreach (HidDeviceInfo info in interfaces)
        {
            RazerMouseModel? razer = RazerCatalog.Find(info.VendorId, info.ProductId);
            if (razer is not null && info.FeatureReportLength >= RazerCatalog.ProtocolReportLength)
            {
                candidates.Add(new DeviceCandidate(DeviceFamily.Razer, info, razer.Name, razer.Preference));
                continue;
            }

            if (RapooVt9Pro.MatchesInterface(info))
            {
                candidates.Add(new DeviceCandidate(DeviceFamily.Rapoo, info, RapooVt9Pro.ModelName, 1));
                continue;
            }

            if (EdifierHecate.MatchesInterface(info))
            {
                candidates.Add(new DeviceCandidate(DeviceFamily.Edifier, info, EdifierHecate.ModelName, 2));
                continue;
            }

            if (GanssKeyboard.MatchesInterface(info))
            {
                candidates.Add(new DeviceCandidate(DeviceFamily.Ganss, info, GanssKeyboard.ModelName, 3));
                continue;
            }

            if (NintendoSwitchPro.MatchesInterface(info))
            {
                candidates.Add(new DeviceCandidate(DeviceFamily.Nintendo, info, NintendoSwitchPro.ModelName, 4));
            }
        }

        List<DeviceCandidate> ranked = candidates
            .OrderBy(c => c.Family)
            .ThenBy(c => c.Preference)
            .ThenBy(c => c.Info.VendorId)
            .ToList();

        log.Debug($"设备候选 {ranked.Count} 个：" +
                 (ranked.Count == 0 ? "无" : string.Join("；", ranked.Select(c => c.Describe()))));
        return ranked;
    }
}
