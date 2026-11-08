using System.Runtime.InteropServices;
using RazerMini.Core.Devices;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;

namespace RazerMini.Platform;

/// <summary><c>XINPUT_BATTERY_INFORMATION</c>：两个字节，类型 + 档位。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct XInputBatteryInformation
{
    public byte BatteryType;
    public byte BatteryLevel;
}

/// <summary>
/// XInput 电量查询（**Xbox 手柄唯一的用户态电量来源**）。
///
/// 为什么只能这样拿：Xbox 手柄的电量走微软私有 GIP 协议，Windows 上由 <c>xboxgip.sys</c> 独占，
/// 用户态碰不到那个通道；但 XInput 自己把电池档位暴露出来了。参考实现
/// `NiyaShy/XB1ControllerBatteryIndicator`（支持 1537/1697/1698/1708/1797/1914 + 官方/第三方接收器）
/// 用的就是这套 API，并明确说蓝牙连不上／读数错误。
///
/// ⚠️ 只有 4 档（空/低/中/满），**没有百分比**。想要精确值必须替换驱动去说 GIP，
/// 那会破坏游戏里的正常使用，本项目不做（见 README §8.3）。
/// </summary>
internal static class XInput
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorDeviceNotConnected = 1167;
    private const byte BatteryDevTypeGamepad = 0;

    /// <summary>xinput1_4（Win8+）优先，退回 xinput1_3（Win7 时代的最后一代带电量 API 的实现）。</summary>
    private static readonly string[] DllCandidates = { "xinput1_4.dll", "xinput1_3.dll" };

    public static bool TryReadBattery(uint slot, out XboxBatteryType type, out XboxBatteryLevel level,
        out int win32Error, out string error)
    {
        type = XboxBatteryType.Disconnected;
        level = XboxBatteryLevel.Empty;
        win32Error = 0;
        error = string.Empty;

        string lastProblem = "没有可用的 xinput dll";
        foreach (string dll in DllCandidates)
        {
            try
            {
                uint code = GetBatteryInformation(dll, slot, BatteryDevTypeGamepad, out XInputBatteryInformation info);
                win32Error = (int)code;
                if (code == ErrorSuccess)
                {
                    type = (XboxBatteryType)info.BatteryType;
                    level = (XboxBatteryLevel)(info.BatteryLevel & 0x03);
                    return true;
                }

                if (code == ErrorDeviceNotConnected)
                {
                    error = "该槽位没有已连接的手柄";
                    return false;
                }

                lastProblem = $"{dll} 返回 Win32={code}";
            }
            catch (DllNotFoundException ex)
            {
                lastProblem = $"{dll} 不存在（{ex.Message}）";
            }
            catch (EntryPointNotFoundException ex)
            {
                lastProblem = $"{dll} 没有导出 XInputGetBatteryInformation（{ex.Message}）";
            }
        }

        error = lastProblem;
        win32Error = -1;
        return false;
    }

    private static uint GetBatteryInformation(string dll, uint slot, byte devType,
        out XInputBatteryInformation info) =>
        dll switch
        {
            "xinput1_4.dll" => XInput14.XInputGetBatteryInformation(slot, devType, out info),
            _ => XInput13.XInputGetBatteryInformation(slot, devType, out info),
        };

    private static class XInput14
    {
        [DllImport("xinput1_4.dll")]
        internal static extern uint XInputGetBatteryInformation(uint userIndex, byte devType,
            out XInputBatteryInformation batteryInformation);
    }

    private static class XInput13
    {
        [DllImport("xinput1_3.dll")]
        internal static extern uint XInputGetBatteryInformation(uint userIndex, byte devType,
            out XInputBatteryInformation batteryInformation);
    }
}

/// <summary>
/// 一个已连接、且能读到电量的 Xbox 手柄。
///
/// 它不走 HID（没有 <see cref="IHidTransport"/>），每次读都是一次 <c>XInputGetBatteryInformation</c>
/// 调用 —— 微秒级、无 I/O，所以托盘每轮都读也不会有一点点负担。
/// </summary>
internal sealed class XboxControllerDevice : IBatteryDevice
{
    private readonly uint _slot;
    private readonly ILog _log;

    /// <summary>上一次读到的档位（0..3），只用于"档位变化"日志。</summary>
    private int? _lastLevel;

    public XboxControllerDevice(uint slot, ILog? log = null)
    {
        _slot = slot;
        _log = log ?? NullLog.Instance;
        _log.Info($"登记 Xbox 手柄：XInput 槽 {_slot + 1}（电量来自 XInputGetBatteryInformation，只有 4 档）");
    }

    public string DisplayName => XboxControllerDiscovery.ModelName(_slot);
    public string DeviceId => $"xinput:{_slot}";
    public int MaxDpi => 0;
    public bool SupportsDpi => false;
    public bool SupportsBattery => true;

    /// <summary>XInput 侧没有"写"的概念。</summary>
    public bool CanWrite => false;

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error) =>
        TryReadBattery(scale, out sample, out error, out _);

    public bool TryReadBattery(BatteryScale scale, out BatterySample sample, out string error, out ReadOutcome outcome)
    {
        sample = default;
        error = string.Empty;
        outcome = ReadOutcome.NoResponse;

        if (!XInput.TryReadBattery(_slot, out XboxBatteryType type, out XboxBatteryLevel level, out int win32Error,
                out string xinputError))
        {
            // 1167 = 手柄关机/断连；其余（dll 缺失等）算失败
            outcome = win32Error == 1167 ? ReadOutcome.NoResponse : ReadOutcome.Failure;
            error = xinputError;
            return false;
        }

        if (type == XboxBatteryType.Wired)
        {
            error = "该手柄当前是有线供电（USB 直连），没有电池可读";
            outcome = ReadOutcome.Unsupported;
            return false;
        }

        if (!XboxBattery.HasBattery(type) || type == XboxBatteryType.Disconnected)
        {
            // 刚连上时 Windows 会先报一段"电池未知"，过几秒/按一下键才给真值
            error = $"电池信息还没就绪（{XboxBattery.DescribeType(type)}），按一下手柄键或等几秒";
            outcome = ReadOutcome.NoResponse;
            return false;
        }

        int percent = XboxBattery.ToPercent(level);

        // 4 档制：档位变化才记日志（默认不倒垃圾，但用户事后能查"什么时候掉的档"）
        int? previousLevel = _lastLevel;
        _lastLevel = (int)level;
        if (previousLevel is not null && previousLevel != (int)level)
        {
            _log.Info($"Xbox 手柄（槽 {_slot + 1}）电量档位变化：" +
                      $"{XboxBattery.DescribeLevel((XboxBatteryLevel)previousLevel.Value)} → {XboxBattery.DescribeLevel(level)}" +
                      $"（{XboxBattery.ToPercent((XboxBatteryLevel)previousLevel.Value)}% → {percent}%）");
        }

        sample = new BatterySample(
            Percent: percent,
            RawValue: (byte)level,
            Charging: null,                       // XInput 不报充电状态
            Timestamp: DateTimeOffset.Now,
            Scale: BatteryScale.Direct,
            RawHex: $"type={(byte)type} level={(byte)level}（{XboxBattery.DescribeType(type)} / " +
                    $"{XboxBattery.DescribeLevel(level)}）")
        {
            LevelText = $"{XboxBattery.DescribeLevel(level)} {(int)level + 1}/4 档",
            StageLevel = (int)level,
            StageCount = 4,
        };

        error = string.Empty;
        outcome = ReadOutcome.Ok;
        _log.Debug($"Xbox 电量：{XboxBattery.DescribeLevel(level)} → {percent}%（{XboxBattery.DescribeType(type)}）");
        return true;
    }

    public bool TryReadDpi(out int dpiX, out int dpiY, out string error)
    {
        dpiX = 0;
        dpiY = 0;
        error = "Xbox 手柄没有 DPI 可读（只读电量）";
        return false;
    }

    public bool TryWriteDpi(int dpiX, int dpiY, out string error)
    {
        error = "Xbox 手柄不支持设置 DPI（只读电量）";
        return false;
    }

    public void Dispose()
    {
        // 没有句柄要释放：XInput 是进程级 API
    }
}

/// <summary>
/// 发现 XInput 槽 0..3 上的手柄。只登记**有电池可读**的槽：
/// 有线直连（BATTERY_TYPE_WIRED）说明由 USB 供电、根本没有电池，不该出现在"电量"列表里。
/// </summary>
internal static class XboxControllerDiscovery
{
    private const int MaxSlots = 4;

    /// <summary>微软 VID。Xbox 手柄/适配器全是 045E；诊断里用它筛 HID 与蓝牙设备。</summary>
    public const ushort MicrosoftVendorId = 0x045E;

    /// <summary>
    /// 显示名。**故意不带槽位和电池类型**（原来拼成"Xbox Xbox 手柄（槽 1） · 碱性电池（AA）"，
    /// 用户明确要求只显示"Xbox 手柄"）。槽位仍在 Path/Id 里，诊断和多手柄区分不受影响。
    /// </summary>
    public static string ModelName(uint slot) => "Xbox 手柄";

    /// <summary>一个槽位的当前状态（不含日志，供 3 秒哨兵轮询）。</summary>
    public readonly record struct XboxSlotState(
        uint Slot,
        XboxBatteryType Type,
        XboxBatteryLevel Level,
        bool HasBattery,
        int Percent);

    /// <summary>
    /// 只读扫描 4 个槽位，**不写日志**。XInput 调用是微秒级的，可以每 3 秒跑一次；
    /// 手柄开机/连线走 XBOXGIP，不会触发 HID 的 WM_DEVICECHANGE，只能靠这个哨兵发现。
    /// </summary>
    public static List<XboxSlotState> Scan(ILog? log = null)
    {
        var states = new List<XboxSlotState>(MaxSlots);

        for (uint slot = 0; slot < MaxSlots; slot++)
        {
            if (!XInput.TryReadBattery(slot, out XboxBatteryType type, out XboxBatteryLevel level, out int win32Error,
                    out string error))
            {
                // 1167 = 该槽没有手柄，属常态；其它错误留给调用方决定要不要记
                if (log is not null && win32Error != 1167)
                {
                    log.Warn($"XInput 槽 {slot + 1} 读取失败：{error}");
                }

                continue;
            }

            states.Add(new XboxSlotState(slot, type, level, XboxBattery.HasBattery(type),
                XboxBattery.ToPercent(level)));
        }

        return states;
    }

    /// <summary>合成的接口路径前缀。托盘据此把这类"非 HID 设备"排除在"接口消失"清理之外。</summary>
    public const string PathPrefix = "xinput://";

    public static List<ConnectedDevice> Discover(ILog log)
    {
        var found = new List<ConnectedDevice>();

        foreach (XboxSlotState state in Scan(log))
        {
            uint slot = state.Slot;

            if (!state.HasBattery)
            {
                log.Info($"XInput 槽 {slot + 1}：{XboxBattery.DescribeType(state.Type)}，不登记（没有可读电量）");
                continue;
            }

            var info = new HidDeviceInfo
            {
                Path = $"{PathPrefix}slot{slot}",
                VendorId = MicrosoftVendorId,
                ProductId = 0x0000,       // XInput 不告诉型号，见 IdOverride
                Opened = true,
            };

            var candidate = new DeviceCandidate(
                DeviceFamily.Xbox, info, ModelName(slot),
                Preference: 0, IdOverride: $"xinput:{slot}");

            log.Info($"发现 Xbox 手柄：槽 {slot + 1}，{XboxBattery.DescribeType(state.Type)}，" +
                     $"{XboxBattery.DescribeLevel(state.Level)}（{state.Percent}%）");

            found.Add(new ConnectedDevice
            {
                Candidate = candidate,
                Device = new XboxControllerDevice(slot, log),
            });
        }

        return found;
    }
}
