using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RazerMini.Core.Rendering;
using RazerMini.Interop;

namespace RazerMini.Platform;

/// <summary>
/// 把 Core 算好的 ARGB 像素缓冲落成 <see cref="Icon"/>。
/// 图标句柄要手动 DestroyIcon，否则每次刷新都泄漏一个 GDI 句柄（托盘长期驻留会积累）。
/// </summary>
internal static class IconFactory
{
    public static Icon Create(TrayImage image)
    {
        using var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(
            new Rectangle(0, 0, image.Width, image.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(image.Pixels, 0, data.Scan0, image.Pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            // Icon.FromHandle 不接管句柄所有权，必须 Clone 之后自己销毁
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            HidNative.DestroyIcon(handle);
        }
    }

    public static Icon Build(TrayIconMode mode, int size, TrayVisualState state) =>
        Create(TrayImageFactory.Render(mode, size, state));
}
