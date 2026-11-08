using System.Runtime.InteropServices;

namespace RazerMini.Interop;

/// <summary>
/// 桌面浮窗用到的窗口级操作：点击穿透、层叠顺序、圆角、以及"临时隐身"（截屏做玻璃底时用）。
///
/// 注意：**毛玻璃不走 DWM**。点击穿透必须给窗口加 `WS_EX_LAYERED`，
/// 而 layered 窗口上 accent policy（acrylic/blur）不生效 —— 真机表现为"完全没有毛玻璃"。
/// 玻璃由 ScreenBackdrop 自己截屏 + 模糊 + 叠色实现。
/// </summary>
internal static class WindowEffects
{
    private const int GwlExStyle = -20;

    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoActivate = 0x08000000;

    private const int LwaAlpha = 0x00000002;

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;
    private const int DwmwaUseImmersiveDarkMode = 20;

    private const int SwpNoSize = 0x0002;
    private const int SwpNoMove = 0x0001;
    private const int SwpNoZOrder = 0x0004;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, int colorKey, byte alpha, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, int flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 点击穿透。**WS_EX_LAYERED 必须常驻**：只加 WS_EX_TRANSPARENT 时跨进程命中测试不认账，
    /// `WM_NCHITTEST` 返回 `HTTRANSPARENT` 也只在同线程窗口之间有效。
    /// 而 layered 常驻还有一个用处：截屏做玻璃底时能把窗口临时调到 alpha=1 "隐身"，
    /// 否则交互模式下截屏会把自己也拍进去（玻璃里叠一层自己的影子）。
    /// 这里只切 TRANSPARENT 一位，alpha 固定 255 = 画面原样显示。
    /// </summary>
    public static void SetClickThrough(IntPtr hwnd, bool enabled)
    {
        if (hwnd == IntPtr.Zero) return;

        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate | WsExLayered;

        if (enabled) style |= WsExTransparent;
        else style &= ~(long)WsExTransparent;

        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));

        SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);

        // SWP_NOMOVE|SWP_NOSIZE 一个都不能少：漏掉它们等于让窗口挪到 (0,0) 并缩成 0x0
        // （真机表现就是"点一下交互开关，浮窗跳回左上角、尺寸也没了"）。
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    /// <summary>layered 窗口的常量 alpha。截屏做玻璃底时临时调到 1（肉眼不可见），抓完恢复 255。</summary>
    public static void SetLayeredAlpha(IntPtr hwnd, byte alpha)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = SetLayeredWindowAttributes(hwnd, 0, alpha, LwaAlpha);
    }

    /// <summary>把窗口提到普通窗口层最前（不是置顶，别抢 TopMost 的位置）。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>沉到 z-order 最底：桌面挂件不该挡住任何窗口。</summary>
    public static void SendToBottom(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>写 ex-style（创建期用，避免窗口已显示后再改动引起闪烁）。</summary>
    public static void ApplyBaseStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate | WsExLayered;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
    }

    /// <summary>
    /// Win11 圆角 + 深色窗口语义。没有 DWM 圆角时（Win10）就是直角卡片，
    /// 但玻璃/描边/留白都还在，不至于难看。
    /// </summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int round = DwmwcpRound;
            _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));

            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        }
        catch (EntryPointNotFoundException)
        {
            // 老系统没有这些属性，忽略
        }
        catch (DllNotFoundException)
        {
            // 理论不会发生
        }
    }

    /// <summary>把当前 ex-style 拼成可读字符串（诊断报告用）。</summary>
    public static string DescribeStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(无句柄)";

        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var flags = new List<string>();
        if ((style & WsExTransparent) != 0) flags.Add("TRANSPARENT");
        if ((style & WsExLayered) != 0) flags.Add("LAYERED");
        if ((style & WsExToolWindow) != 0) flags.Add("TOOLWINDOW");
        if ((style & WsExNoActivate) != 0) flags.Add("NOACTIVATE");
        if ((style & 0x00000008) != 0) flags.Add("TOPMOST");
        return $"0x{style:X8} [{string.Join("|", flags)}]";
    }
}
