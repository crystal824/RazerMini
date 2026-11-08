using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RazerMini.Interop;

namespace RazerMini.Platform;

/// <summary>
/// 自己算的"毛玻璃"背景：把窗口背后的屏幕内容截下来、强模糊、再交给上层叠色。
///
/// 为什么不用 DWM 的 acrylic：**点击穿透必须给窗口加 `WS_EX_LAYERED`，
/// 而 layered 窗口上 accent policy（acrylic/blur）根本不生效** —— 两者本质冲突。
/// 真机反馈"完全没有毛玻璃效果"就是这个原因。自己渲染则两者都要得到。
///
/// 截屏时先把自己调成近乎全透明（alpha=1/255，肉眼不可见）再抓屏，避免把自己的像素糊进背景；
/// 抓完立刻恢复 255。整个动作 ~5ms，用户看不到。
///
/// 模糊走"降采样 + 多次 box blur + 升采样"：比高斯快一个数量级，视觉上足够（磨砂本来就该糊）。
/// </summary>
internal static class ScreenBackdrop
{
    private const int Downscale = 4;
    private const int BlurPasses = 2;
    private const int BlurRadius = 2;

    /// <summary>
    /// 抓取 <paramref name="bounds"/> 处的屏幕内容并模糊。失败返回 null（调用方退纯色面板）。
    /// </summary>
    public static Bitmap? Capture(IntPtr hwnd, Rectangle bounds)
    {
        int width = Math.Max(1, bounds.Width);
        int height = Math.Max(1, bounds.Height);

        Bitmap? shot = null;
        try
        {
            shot = new Bitmap(width, height, PixelFormat.Format32bppArgb);

            // 先"隐身"：layered 窗口的常量 alpha 调到 1，截屏里就只剩桌面
            WindowEffects.SetLayeredAlpha(hwnd, 1);
            try
            {
                using var graphics = Graphics.FromImage(shot);
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(width, height),
                    CopyPixelOperation.SourceCopy);
            }
            finally
            {
                WindowEffects.SetLayeredAlpha(hwnd, 255);
            }

            int smallWidth = Math.Max(1, width / Downscale);
            int smallHeight = Math.Max(1, height / Downscale);

            using var small = new Bitmap(shot, smallWidth, smallHeight);
            for (int pass = 0; pass < BlurPasses; pass++)
            {
                BoxBlur(small, BlurRadius);
            }

            // 放大回原尺寸：双三次插值相当于又平滑了一次
            var blurred = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(blurred))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(small, new Rectangle(0, 0, width, height),
                    0, 0, smallWidth, smallHeight, GraphicsUnit.Pixel);
            }

            return blurred;
        }
        catch
        {
            return null;
        }
        finally
        {
            shot?.Dispose();
        }
    }

    /// <summary>一次水平/垂直分离的 box blur（就地修改）。</summary>
    private static void BoxBlur(Bitmap bitmap, int radius)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width < 3 || height < 3 || radius < 1) return;

        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            int stride = data.Stride;
            var pixels = new int[width * height];

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * stride), pixels, y * width, width);
            }

            var scratch = new int[Math.Max(width, height)];

            BlurPass(pixels, scratch, width, height, radius, horizontal: true);
            BlurPass(pixels, scratch, width, height, radius, horizontal: false);

            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * width, IntPtr.Add(data.Scan0, y * stride), width);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void BlurPass(int[] pixels, int[] scratch, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        int step = horizontal ? 1 : width;
        int lineStep = horizontal ? width : 1;

        for (int line = 0; line < lines; line++)
        {
            int start = line * lineStep;

            for (int i = 0; i < length; i++)
            {
                int from = Math.Max(0, i - radius);
                int to = Math.Min(length - 1, i + radius);
                int count = to - from + 1;

                int sumB = 0, sumG = 0, sumR = 0;
                for (int j = from; j <= to; j++)
                {
                    int sample = pixels[start + j * step];
                    sumB += sample & 0xFF;
                    sumG += (sample >> 8) & 0xFF;
                    sumR += (sample >> 16) & 0xFF;
                }

                scratch[i] = unchecked((int)0xFF000000)
                             | ((sumR / count) << 16)
                             | ((sumG / count) << 8)
                             | (sumB / count);
            }

            for (int i = 0; i < length; i++)
            {
                pixels[start + i * step] = scratch[i];
            }
        }
    }
}
