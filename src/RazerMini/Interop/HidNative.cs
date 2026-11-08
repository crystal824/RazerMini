using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RazerMini.Interop;

/// <summary>
/// hid.dll / setupapi.dll / kernel32.dll 的最小 P/Invoke 集合。零 NuGet 依赖全靠这一层。
/// </summary>
internal static class HidNative
{
    // ─────────────── hid.dll ───────────────

    [DllImport("hid.dll")]
    internal static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetAttributes(SafeFileHandle device, out HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetProductString(SafeFileHandle device, StringBuilder buffer, int bufferLength);   // bufferLength 单位是**字节**

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetFeature(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetFeature(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

    /// <summary>取最近一条 input report（等价 Linux hidraw 的 HIDIOCGINPUT）。</summary>
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_GetInputReport(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

    /// <summary>
    /// 直接发一条 output report（<c>IOCTL_HID_SET_OUTPUT_REPORT</c>）。
    /// 与 <c>WriteFile</c> 的区别：**这个 IOCTL 是 FILE_ANY_ACCESS**，零权限句柄也能发，
    /// 所以是"打不开读写句柄"时的唯一写通路。缓冲区首字节必须是 report ID。
    /// </summary>
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool HidD_SetOutputReport(SafeFileHandle device, byte[] reportBuffer, int reportBufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool WriteFile(SafeFileHandle hFile, byte[] buffer, int numberOfBytesToWrite,
        out int numberOfBytesWritten, IntPtr lpOverlapped);

    // ─────────────── 异步（overlapped）读：interrupt IN 端点的正道 ───────────────
    //
    // HidD_GetInputReport 走的是 IOCTL_HID_GET_INPUT_REPORT，只返回"驱动缓存里最后一条"，
    // 有些设备的厂商集合根本不填这个缓存 → 每次都回 Win32=31（ERROR_GEN_FAILURE）。
    // 官方驱动/HECATE Connect 实际用的是挂起的 interrupt IN 读（ReadFile + OVERLAPPED），
    // 这里照 hidapi 的做法补上：句柄必须带 FILE_FLAG_OVERLAPPED 打开。

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool ReadFile(SafeFileHandle hFile, byte[] buffer, int numberOfBytesToRead,
        IntPtr lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateEventW(IntPtr lpEventAttributes,
        bool bManualReset, bool bInitialState, IntPtr lpName);   // bool 参数走默认 4 字节 BOOL，别加 U1

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool ResetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool GetOverlappedResult(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped,
        out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CancelIoEx(SafeFileHandle hFile, ref NativeOverlapped lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool CloseHandle(IntPtr hObject);

    // ─────────────── setupapi.dll ───────────────

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // ─────────────── kernel32 / user32 ───────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    // ─────────────── 常量 ───────────────

    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;

    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;

    internal const int ErrorIoPending = 997;
    internal const int ErrorTimeout = 1460;
    internal const uint WaitObject0 = 0;

    internal static readonly IntPtr InvalidHandleValue = new(-1);

    // ─────────────── 结构体 ───────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    /// <summary>
    /// HIDP_CAPS 原文：Usage, UsagePage, In/Out/FeatureReportByteLength, Reserved[17],
    /// 后面一串 NumberXxxCaps / NumberXxxDataIndices。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;

        public static HidpCaps Create() => new() { Reserved = new ushort[17] };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public int Size;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    /// <summary>OVERLAPPED（x64/x86 布局相同，各字段都是指针宽度 / 32 位）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeOverlapped
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public int Offset;
        public int OffsetHigh;
        public IntPtr hEvent;
    }
}
