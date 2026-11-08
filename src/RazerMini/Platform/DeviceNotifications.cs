using System.Runtime.InteropServices;

namespace RazerMini.Platform;

/// <summary>
/// 设备插拔通知（WM_DEVICECHANGE）的订阅者：一个 message-only 窗口 + HID 接口类通知。
///
/// 为什么需要它：原来靠"每 3 次轮询重扫一次"，连接状态下轮询是 60 秒 → 新插的接收器最多要等
/// 3 分钟才出现在浮窗里，用户只能去点"立即刷新"（真机反馈）。订阅之后插拔是秒级响应。
///
/// 订阅范围是**所有设备接口类**（不只是 HID）：Xbox 手柄走 XBOXGIP，
/// 只订阅 HID 会漏掉它开机/连线的事件（真机上表现为"要等下一轮 60 秒轮询"）。
/// 另外 XInput 还有 3 秒哨兵作为兜底（见 TrayApp），两条路一起才追得上 Steam 的发现速度。
/// </summary>
internal sealed class DeviceNotifications : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;

    private const int DbtDeviceTypeDeviceInterface = 5;
    private const int DeviceNotifyWindowHandle = 0;

    /// <summary>
    /// 收"所有设备接口类"的通知（ClassGuid 被忽略）。
    /// 只订阅 HID 类是不够的：Xbox 手柄走 XBOXGIP，开机/连线不会产生 HID 接口事件，
    /// 真机上就只能等下一次轮询（用户对比 Steam：Steam 能立刻发现，我们等了一分钟）。
    /// </summary>
    private const int DeviceNotifyAllInterfaceClasses = 0x00000004;

    private static readonly Guid HidInterfaceClass = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        public char Name;   // 变长数组的占位，注册时用 Size 表示实际大小
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private IntPtr _registration;

    /// <summary>设备接入/拔出（可能成串到达，订阅方需要自己防抖）。</summary>
    public event EventHandler? DevicesChanged;

    public DeviceNotifications()
    {
        var parameters = new CreateParams
        {
            Caption = "RazerMiniDeviceWatch",
            Parent = (IntPtr)(-3),   // HWND_MESSAGE：不可见、不进 z-order
        };

        CreateHandle(parameters);
        Register();
    }

    private void Register()
    {
        int size = Marshal.SizeOf<DevBroadcastDeviceInterface>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            var filter = new DevBroadcastDeviceInterface
            {
                Size = size,
                DeviceType = DbtDeviceTypeDeviceInterface,
                ClassGuid = HidInterfaceClass,
            };

            Marshal.StructureToPtr(filter, buffer, false);
            _registration = RegisterDeviceNotificationW(Handle, buffer,
                DeviceNotifyWindowHandle | DeviceNotifyAllInterfaceClasses);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange)
        {
            long what = m.WParam.ToInt64();
            if (what is DbtDeviceArrival or DbtDeviceRemoveComplete or DbtDevNodesChanged)
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registration != IntPtr.Zero)
        {
            _ = UnregisterDeviceNotification(_registration);
            _registration = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
