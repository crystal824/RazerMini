using RazerMini.Core.Devices;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Platform;

/// <summary>一次"某个候选接口 + 某种访问权限"的尝试结果，诊断报告直接摊开它。</summary>
internal sealed record ConnectAttempt(
    DeviceCandidate Candidate,
    string AccessMode,
    bool Opened,
    bool ReadOk,
    string Detail,
    bool Registered = false)
{
    public string ModelName => Candidate.ModelName;
    public string FamilyName => Candidate.FamilyName;
    public string Id => Candidate.Id;
}

/// <summary>一个连上的设备：候选信息 + 已实例化的驱动。</summary>
internal sealed class ConnectedDevice
{
    public required DeviceCandidate Candidate { get; init; }
    public required IBatteryDevice Device { get; init; }
    public string Path => Candidate.Info.Path;
    public string Key => $"{Candidate.Family}:{Candidate.Info.Path}";
}

/// <summary>
/// 把"枚举 → 按品牌族选通道 → 打开 → 实测读电量"串起来，**逐个候选试到成功为止**，并可同时连多台。
///
/// 为什么要逐个试、且要跨品牌：
/// - 同一台机器可能同时插着 007B 接收器和 007E 底座，两者都有 91 字节通道，但 007E 回 `0x05 不支持`；
/// - 用户同时插着雷蛇/雷柏/漫步者三只鼠标，托盘要能同时显示三块电量。
/// </summary>
internal static class DeviceConnector
{
    public static List<ConnectedDevice> ConnectAll(
        IReadOnlyList<HidDeviceInfo> interfaces,
        ILog log,
        out IReadOnlyList<ConnectAttempt> attempts)
    {
        var attemptLog = new List<ConnectAttempt>();
        attempts = attemptLog;
        var connected = new List<ConnectedDevice>();

        List<DeviceCandidate> candidates = DeviceCatalog.RankCandidates(interfaces, log);
        if (candidates.Count == 0)
        {
            return connected;
        }

        foreach (DeviceCandidate candidate in candidates)
        {
            bool connectedHere = false;

            foreach ((uint access, string modeName) in HidDevice.AccessModes)
            {
                HidDevice? transport = HidDevice.TryOpenWith(access, modeName, candidate.Info, log, out int openError);
                if (transport is null)
                {
                    attemptLog.Add(new ConnectAttempt(candidate, modeName, false, false, $"打不开 Win32={openError}"));
                    continue;
                }

                IBatteryDevice? device = CreateDriver(candidate, transport, log);
                if (device is null)
                {
                    transport.Dispose();
                    attemptLog.Add(new ConnectAttempt(candidate, modeName, true, false, "无法为该接口创建驱动"));
                    continue;
                }

                if (device.TryReadBattery(BatteryScale.Raw255, out BatterySample sample, out string error,
                        out ReadOutcome outcome))
                {
                    attemptLog.Add(new ConnectAttempt(candidate, modeName, true, true,
                        $"raw={sample.RawValue}（{sample.Percent}%），充电={sample.Charging?.ToString() ?? "未知"}"));
                    connected.Add(new ConnectedDevice { Candidate = candidate, Device = device });
                    connectedHere = true;
                    log.Info($"连接成功：{candidate.FamilyName} {candidate.ModelName}（{modeName}句柄），{sample.Percent}%");
                    break;
                }

                // 读不到电量分两种情况，处理完全不同：
                // ① 这条命令设备/固件根本不支持（雷蛇 007E 底座回 0x05）→ 换下一个候选接口；
                // ② 设备是对的、只是**现在**读不到（无线鼠标休眠/关机：雷蛇回 0x04 无应答、
                //    雷柏回不可信应答、键盘整窗无报文）→ **也要登记**。
                //    否则用户一开程序看到的不是"雷柏 0%"，而是"雷柏这个设备不存在"；
                //    而且登记之后，轮询每轮都会再试着读，鼠标一动就能自己恢复。
                //
                // 判据来自驱动给出的 ReadOutcome，**不再从错误文案里 grep**（那是 1.15 之前的做法，
                // 改一句提示就会静默失效）。
                if (outcome == ReadOutcome.NotWritable)
                {
                    // 句柄权限不够（只读/零权限读不到主动查询类设备）→ 换下一种访问权限再试，
                    // 这种情况**不能登记**：拿一个读不了的句柄占着位置，永远读不到数
                    attemptLog.Add(new ConnectAttempt(candidate, modeName, true, false, error));
                    device.Dispose();
                    continue;
                }

                if (outcome == ReadOutcome.Unsupported)
                {
                    attemptLog.Add(new ConnectAttempt(candidate, modeName, true, false, error));
                    device.Dispose();
                    log.Warn($"{candidate.ModelName} 不支持该命令（{outcome.Describe()}），换下一个候选接口");
                    break;
                }

                attemptLog.Add(new ConnectAttempt(candidate, modeName, true, false, error, Registered: true));
                connected.Add(new ConnectedDevice { Candidate = candidate, Device = device });
                connectedHere = true;
                log.Info($"登记设备：{candidate.FamilyName} {candidate.ModelName}（{modeName}句柄），" +
                         $"电量暂时读不到（{outcome.Describe()}）：{error}");
                break;
            }

            if (!connectedHere)
            {
                log.Debug($"候选 {candidate.ModelName} [{candidate.Id}] 未能连上");
            }
        }

        return connected;
    }

    private static IBatteryDevice? CreateDriver(DeviceCandidate candidate, IHidTransport transport, ILog log) =>
        candidate.Family switch
        {
            DeviceFamily.Razer => CreateRazer(candidate, transport, log),
            DeviceFamily.Rapoo => new RapooVt9Pro(transport, log),
            DeviceFamily.Edifier => new EdifierHecate(transport, log),
            DeviceFamily.Ganss => new GanssKeyboard(transport, log),
            DeviceFamily.Nintendo => new NintendoSwitchPro(transport, log),
            _ => null,
        };

    private static IBatteryDevice? CreateRazer(DeviceCandidate candidate, IHidTransport transport, ILog log)
    {
        RazerMouseModel? model = RazerCatalog.Find(candidate.Info.VendorId, candidate.Info.ProductId);
        if (model is null) return null;

        try
        {
            return new RazerMouse(transport, model, log);
        }
        catch (ArgumentException ex)
        {
            log.Warn($"创建雷蛇驱动失败：{ex.Message}");
            return null;
        }
    }
}
