using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;
using RazerMini.Interop;

namespace RazerMini.Platform;

public sealed record HidEnumeration(List<HidDeviceInfo> Devices, int PathCount);

/// <summary>
/// HID 枚举：走 SetupDi + HidD_*，拿 path / VID / PID / usage / 三种 report 长度 / 产品串。
///
/// 铁律：**枚举到的每个接口都必须出现在结果里**，并带上是哪一步、哪个 Win32 错误把它刷掉的。
/// 早期版本在打开失败时直接 return null，结果是"能用的接口没出现、也没有任何线索"——
/// 真机上表现为"8 个雷蛇接口 feat 全是 0"，而 PnP 里明明有 MI_02 厂商接口（2026-10-26 踩过）。
/// </summary>
internal static class HidEnumerator
{
    // 打开失败的伪错误码（负数是 hid.dll 阶段失败的标记，正数是 CreateFile 的 Win32 码）
    private const int ErrorAttributesFailed = -1;
    private const int ErrorPreparsedFailed = -2;
    private const int ErrorCapsFailed = -3;

    public static HidEnumeration EnumerateAll(ILog log, IReadOnlyCollection<ushort>? vendorIds = null)
    {
        var paths = new List<string>();
        HidNative.HidD_GetHidGuid(out Guid hidGuid);

        IntPtr deviceInfoSet = HidNative.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            HidNative.DigcfPresent | HidNative.DigcfDeviceInterface);

        if (deviceInfoSet == HidNative.InvalidHandleValue)
        {
            log.Error($"SetupDiGetClassDevs 失败，Win32 错误 {Marshal.GetLastWin32Error()}");
            return new HidEnumeration(new List<HidDeviceInfo>(), 0);
        }

        try
        {
            var interfaceData = new HidNative.SpDeviceInterfaceData();

            for (uint index = 0; ; index++)
            {
                // 每轮重置 cbSize：SetupDiEnumDeviceInterfaces 会改写其它字段，靠它校验结构版本
                interfaceData.Size = Marshal.SizeOf<HidNative.SpDeviceInterfaceData>();
                if (!HidNative.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != 259 /* ERROR_NO_MORE_ITEMS */)
                    {
                        log.Warn($"SetupDiEnumDeviceInterfaces 在第 {index} 项终止，Win32 错误 {error}");
                    }
                    break;
                }

                string? path = GetDevicePath(deviceInfoSet, ref interfaceData, log);
                if (path is null) continue;

                // 接口路径形如 \\?\hid#vid_1532&pid_007b&mi_02#...（USB）或
                // \\?\hid#{00001124-…}_vid&0002057e&pid&2009#...（蓝牙），可以按 VID 预过滤，
                // 省掉几百次 CreateFile（断线重扫时每 5 秒跑一次，这个开销不小）。
                // ⚠️ 必须走 HidPath：蓝牙路径里 VID 是 "vid&0002xxxx" 形态，
                // 只匹配 "vid_xxxx" 会把 Switch Pro 这类蓝牙手柄整族漏掉（2026-11-02 真机踩到）。
                if (vendorIds is { Count: > 0 })
                {
                    bool matched = false;
                    foreach (ushort vid in vendorIds)
                    {
                        if (HidPath.MatchesVendor(path, vid))
                        {
                            matched = true;
                            break;
                        }
                    }

                    if (!matched) continue;
                }

                paths.Add(path);
            }
        }
        finally
        {
            HidNative.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        var devices = new List<HidDeviceInfo>(paths.Count);
        foreach (string path in paths)
        {
            HidDeviceInfo info = DescribeDevice(path, log);
            devices.Add(info);
        }

        int locked = devices.Count(d => !d.Opened);
        log.Debug($"HID 枚举完成：路径 {paths.Count} 个，接口 {devices.Count} 个（其中 {locked} 个打不开/读不到）");
        return new HidEnumeration(devices, paths.Count);
    }

    /// <summary>只枚举指定 VID 的接口（三只鼠标分属三个 VID，预过滤省掉几百次 CreateFile）。</summary>
    public static HidEnumeration EnumerateForVendors(ILog log, params ushort[] vendorIds) =>
        EnumerateAll(log, vendorIds);

    /// <summary>只保留雷蛇 VID 的接口（兼容旧调用）。</summary>
    public static List<HidDeviceInfo> EnumerateRazer(ILog log) =>
        EnumerateAll(log, new ushort[] { 0x1532 }).Devices;

    private static string? GetDevicePath(IntPtr deviceInfoSet, ref HidNative.SpDeviceInterfaceData interfaceData, ILog log)
    {
        // SP_DEVICE_INTERFACE_DETAIL_DATA_W：{ DWORD cbSize; WCHAR DevicePath[]; }
        // x64 下 cbSize = 8（DWORD + 对齐），x86 下 6（DWORD + 1 个 WCHAR）
        const int bufferSize = 2048;
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);

            if (!HidNative.SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, buffer,
                    bufferSize, out uint required, IntPtr.Zero))
            {
                log.Warn($"SetupDiGetDeviceInterfaceDetail 失败，Win32 错误 {Marshal.GetLastWin32Error()}（需要 {required} 字节）");
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>任何一步失败都返回带错误标记的对象，绝不返回 null（否则问题会凭空消失）。</summary>
    private static HidDeviceInfo DescribeDevice(string path, ILog log)
    {
        (SafeFileHandle? handle, string mode, int error) = OpenWithFallback(path);
        if (handle is null)
        {
            (ushort vid, ushort pid) = ParseVidPid(path);
            log.Warn($"接口三种访问模式都打不开 Win32={error}：{path}");
            return new HidDeviceInfo
            {
                Path = path,
                VendorId = vid,
                ProductId = pid,
                Opened = false,
                OpenError = error,
            };
        }

        using SafeFileHandle owned = handle;

        var attributes = new HidNative.HidAttributes { Size = (uint)Marshal.SizeOf<HidNative.HidAttributes>() };
        if (!HidNative.HidD_GetAttributes(owned, out attributes))
        {
            int attrError = Marshal.GetLastWin32Error();
            (ushort vid, ushort pid) = ParseVidPid(path);
            log.Warn($"HidD_GetAttributes 失败 Win32={attrError}：{path}");
            return new HidDeviceInfo
            {
                Path = path,
                VendorId = vid,
                ProductId = pid,
                Opened = false,
                OpenError = ErrorAttributesFailed,
            };
        }

        var caps = HidNative.HidpCaps.Create();
        if (!HidNative.HidD_GetPreparsedData(owned, out IntPtr preparsed))
        {
            int preparsedError = Marshal.GetLastWin32Error();
            log.Warn($"HidD_GetPreparsedData 失败 Win32={preparsedError}：{path}");
            return new HidDeviceInfo
            {
                Path = path,
                VendorId = attributes.VendorID,
                ProductId = attributes.ProductID,
                Opened = false,
                OpenError = ErrorPreparsedFailed,
            };
        }

        try
        {
            if (HidNative.HidP_GetCaps(preparsed, ref caps) != 0x00110000 /* HIDP_STATUS_SUCCESS */)
            {
                log.Warn($"HidP_GetCaps 失败：{path}");
                return new HidDeviceInfo
                {
                    Path = path,
                    VendorId = attributes.VendorID,
                    ProductId = attributes.ProductID,
                    Opened = false,
                    OpenError = ErrorCapsFailed,
                };
            }
        }
        finally
        {
            HidNative.HidD_FreePreparsedData(preparsed);
        }

        string product = string.Empty;
        var sb = new StringBuilder(256);
        // 长度参数单位是字节，不是字符（参考实现用 char[126] + length*2）
        if (HidNative.HidD_GetProductString(owned, sb, sb.Capacity * sizeof(char)))
        {
            product = sb.ToString();
        }

        return new HidDeviceInfo
        {
            Path = path,
            VendorId = attributes.VendorID,
            ProductId = attributes.ProductID,
            UsagePage = caps.UsagePage,
            Usage = caps.Usage,
            InputReportLength = caps.InputReportByteLength,
            OutputReportLength = caps.OutputReportByteLength,
            FeatureReportLength = caps.FeatureReportByteLength,
            ProductName = product,
            Opened = true,
            OpenMode = mode,
        };
    }

    /// <summary>
    /// 依次尝试 读写 → 只读 → 仅查询(access=0)。
    /// 鼠标类集合常被系统鼠标驱动独占，读写会拒；厂商集合一般读写就能开。
    /// access=0 只够读出 caps，但足以让诊断看见"这个接口到底有没有 91 字节 feature"。
    /// </summary>
    private static (SafeFileHandle? Handle, string Mode, int Error) OpenWithFallback(string path)
    {
        (uint Access, string Name)[] modes =
        {
            (HidNative.GenericRead | HidNative.GenericWrite, "读写"),
            (HidNative.GenericRead, "只读"),
            (0u, "仅查询"),
        };

        int lastError = 0;
        foreach ((uint access, string name) in modes)
        {
            SafeFileHandle handle = HidNative.CreateFile(path, access,
                HidNative.FileShareRead | HidNative.FileShareWrite,
                IntPtr.Zero, HidNative.OpenExisting, 0, IntPtr.Zero);

            if (!handle.IsInvalid)
            {
                return (handle, name, 0);
            }

            lastError = Marshal.GetLastWin32Error();
            handle.Dispose();
        }

        return (null, string.Empty, lastError);
    }

    /// <summary>
    /// 从接口路径抠出 VID/PID。实现在 <see cref="HidPath"/>（Core 里，可被断言覆盖），
    /// 因为 USB 的 <c>vid_1532</c> 与蓝牙的 <c>vid&amp;0002057e</c> 是两种形态。
    /// </summary>
    private static (ushort Vid, ushort Pid) ParseVidPid(string path) => HidPath.Parse(path);
}
