namespace RazerMini.Core.Rendering;

public enum TrayIconMode
{
    /// <summary>托盘图标直接画数字（74 / 100 / --）。</summary>
    Percent = 0,

    /// <summary>托盘图标画电池形状，按电量填充。</summary>
    Battery = 1,
}

/// <summary>渲染托盘图标所需的最小状态。</summary>
public readonly record struct TrayVisualState(bool Connected, int? Percent, bool Charging)
{
    public static TrayVisualState Disconnected => new(false, null, false);
}

/// <summary>一块 ARGB32 像素缓冲（非预乘），由平台层拷进 Bitmap。</summary>
public sealed class TrayImage
{
    public TrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new int[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int[] Pixels { get; }

    public int GetPixel(int x, int y) => Pixels[y * Width + x];

    public void SetPixel(int x, int y, int argb)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        Pixels[y * Width + x] = argb;
    }

    /// <summary>统计不透明像素数（测试断言用）。</summary>
    public int OpaqueCount() => Pixels.Count(p => (p >> 24) != 0);
}

/// <summary>
/// 3x5 像素字体 + 电池图形，纯算术、无 GDI 依赖，所以能在 Linux 上跑断言。
/// Windows 层只负责把 <see cref="TrayImage"/> 拷成 Bitmap → Icon。
/// </summary>
public static class TrayImageFactory
{
    // 每个字符 5 行，每行 3 位（bit2 = 左、bit1 = 中、bit0 = 右）
    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['0'] = new byte[] { 0b111, 0b101, 0b101, 0b101, 0b111 },
        ['1'] = new byte[] { 0b010, 0b110, 0b010, 0b010, 0b111 },
        ['2'] = new byte[] { 0b111, 0b001, 0b111, 0b100, 0b111 },
        ['3'] = new byte[] { 0b111, 0b001, 0b111, 0b001, 0b111 },
        ['4'] = new byte[] { 0b101, 0b101, 0b111, 0b001, 0b001 },
        ['5'] = new byte[] { 0b111, 0b100, 0b111, 0b001, 0b111 },
        ['6'] = new byte[] { 0b111, 0b100, 0b111, 0b101, 0b111 },
        ['7'] = new byte[] { 0b111, 0b001, 0b001, 0b001, 0b001 },
        ['8'] = new byte[] { 0b111, 0b101, 0b111, 0b101, 0b111 },
        ['9'] = new byte[] { 0b111, 0b101, 0b111, 0b001, 0b111 },
        ['-'] = new byte[] { 0b000, 0b000, 0b111, 0b000, 0b000 },
        ['?'] = new byte[] { 0b111, 0b001, 0b011, 0b000, 0b010 },
    };

    private const int GlyphWidth = 3;
    private const int GlyphHeight = 5;

    // 颜色（ARGB）
    private const int ColorGreen = unchecked((int)0xFF4CAF50);
    private const int ColorAmber = unchecked((int)0xFFF5A623);
    private const int ColorRed = unchecked((int)0xFFE53935);
    private const int ColorCharging = unchecked((int)0xFF00BCD4);
    private const int ColorGray = unchecked((int)0xFF8C8C8C);   // 比 9E9E9E 深一档，浅色任务栏上也看得清

    public static int ColorFor(TrayVisualState state)
    {
        if (!state.Connected || state.Percent is null) return ColorGray;
        if (state.Charging) return ColorCharging;
        int pct = state.Percent.Value;
        if (pct >= 50) return ColorGreen;
        if (pct >= 20) return ColorAmber;
        return ColorRed;
    }

    public static string TextFor(TrayVisualState state)
    {
        if (!state.Connected || state.Percent is null) return "--";
        return Math.Clamp(state.Percent.Value, 0, 100).ToString();
    }

    public static TrayImage Render(TrayIconMode mode, int size, TrayVisualState state)
    {
        if (size < 8) size = 8;
        return mode == TrayIconMode.Battery ? RenderBattery(size, state) : RenderPercent(size, state);
    }

    private static TrayImage RenderPercent(int size, TrayVisualState state)
    {
        var image = new TrayImage(size, size);
        string text = TextFor(state);
        int scale = ChooseScale(text.Length, size);
        int textWidth = text.Length * GlyphWidth * scale + (text.Length - 1) * scale;
        int textHeight = GlyphHeight * scale;
        int originX = (size - textWidth) / 2;
        int originY = (size - textHeight) / 2;
        DrawText(image, text, originX, originY, scale, ColorFor(state));
        return image;
    }

    private static TrayImage RenderBattery(int size, TrayVisualState state)
    {
        var image = new TrayImage(size, size);
        int color = ColorFor(state);

        int margin = Math.Max(1, size / 16);
        int nubWidth = Math.Max(1, size / 8);
        int nubHeight = Math.Max(2, (size - 2 * margin) / 3);

        int bodyX = margin;
        int bodyY = margin;
        int bodyW = size - 2 * margin - nubWidth;
        int bodyH = size - 2 * margin;
        if (bodyW < 4 || bodyH < 4) return RenderPercent(size, state);

        // 外壳
        for (int x = bodyX; x < bodyX + bodyW; x++)
        {
            image.SetPixel(x, bodyY, color);
            image.SetPixel(x, bodyY + bodyH - 1, color);
        }
        for (int y = bodyY; y < bodyY + bodyH; y++)
        {
            image.SetPixel(bodyX, y, color);
            image.SetPixel(bodyX + bodyW - 1, y, color);
        }

        // 正极凸起
        int nubX = bodyX + bodyW;
        int nubY = bodyY + (bodyH - nubHeight) / 2;
        for (int x = nubX; x < nubX + nubWidth; x++)
        {
            for (int y = nubY; y < nubY + nubHeight; y++)
            {
                image.SetPixel(x, y, color);
            }
        }

        // 电量填充
        int innerX = bodyX + 1;
        int innerY = bodyY + 1;
        int innerW = bodyW - 2;
        int innerH = bodyH - 2;
        if (innerW > 0 && innerH > 0 && state.Connected && state.Percent is not null)
        {
            int pct = Math.Clamp(state.Percent.Value, 0, 100);
            int fillW = (innerW * pct + 50) / 100;
            if (pct > 0 && fillW == 0) fillW = 1;
            for (int x = innerX; x < innerX + fillW; x++)
            {
                for (int y = innerY; y < innerY + innerH; y++)
                {
                    image.SetPixel(x, y, color);
                }
            }
        }

        return image;
    }

    /// <summary>挑一个既放得下又尽量大的字号（1..3 倍）。</summary>
    public static int ChooseScale(int charCount, int size)
    {
        for (int scale = 3; scale >= 1; scale--)
        {
            int width = charCount * GlyphWidth * scale + Math.Max(0, charCount - 1) * scale;
            int height = GlyphHeight * scale;
            if (width <= size - 2 && height <= size - 2) return scale;
        }
        return 1;
    }

    public static void DrawText(TrayImage image, string text, int originX, int originY, int scale, int argb)
    {
        int cursorX = originX;
        foreach (char c in text)
        {
            if (Glyphs.TryGetValue(c, out var rows))
            {
                for (int row = 0; row < GlyphHeight; row++)
                {
                    byte bits = rows[row];
                    for (int col = 0; col < GlyphWidth; col++)
                    {
                        bool on = (bits & (1 << (GlyphWidth - 1 - col))) != 0;
                        if (!on) continue;
                        for (int dy = 0; dy < scale; dy++)
                        {
                            for (int dx = 0; dx < scale; dx++)
                            {
                                image.SetPixel(cursorX + col * scale + dx, originY + row * scale + dy, argb);
                            }
                        }
                    }
                }
            }
            cursorX += GlyphWidth * scale + scale;
        }
    }
}
