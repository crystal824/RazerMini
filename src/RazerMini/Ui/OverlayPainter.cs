using System.Drawing.Drawing2D;
using System.Drawing.Text;
using RazerMini.Core.Overlay;

namespace RazerMini.Ui;

/// <summary>画一帧浮窗需要的全部外部状态。</summary>
internal sealed class OverlayPaintOptions
{
    /// <summary>是否画玻璃底（用 <c>backdrop</c> 那张模糊图）；false = 纯色面板。</summary>
    public bool Glass { get; init; }

    public bool Interactive { get; init; }
    public double Scale { get; init; } = 1;
}

/// <summary>
/// 字体缓存。频繁 new Font 会漏 GDI 句柄（项目里托盘图标已经踩过一次），按像素字号缓存。
/// 只挑**等宽**字体：数字宽度固定，电量跳动时文字不会左右抖。
/// </summary>
internal sealed class OverlayFonts : IDisposable
{
    private readonly Dictionary<(int Size, bool Bold), Font> _cache = new();
    private readonly string _family;

    public OverlayFonts()
    {
        _family = PickFamily();
    }

    public string Family => _family;

    private static string PickFamily()
    {
        foreach (string name in new[] { "Segoe UI Variable Text", "Segoe UI", "Cascadia Mono", "Consolas", "Microsoft YaHei UI" })
        {
            try
            {
                using var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular)) return name;
            }
            catch (ArgumentException)
            {
                // 这台机器没装这个字体，换下一个
            }
        }

        return FontFamily.GenericSansSerif.Name;
    }

    public Font Get(float pixelSize, bool bold)
    {
        int size = Math.Clamp((int)Math.Round(pixelSize), 6, 96);
        var key = (size, bold);
        if (_cache.TryGetValue(key, out Font? cached)) return cached;

        var font = new Font(_family, size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        _cache[key] = font;
        return font;
    }

    public void Dispose()
    {
        foreach (Font font in _cache.Values) font.Dispose();
        _cache.Clear();
    }
}

/// <summary>
/// 浮窗绘制。几何全部来自 <see cref="OverlayLayout"/>（可在 Linux 上断言），这里只做 GDI+ 落地。
///
/// 观感对齐 Windows 11 系统卡片：上面一层是"模糊过的桌面"（由 ScreenBackdrop 现场截屏算出），
/// 盖一层深色半透明叠色，再压一条 1px 描边和顶部高光；内容按行排布，行间极淡分隔线。
/// **没有标题行、没有卡片边框、没有按钮、没有任何动画。**
///
/// 注意：窗口是 layered（点击穿透要求），DWM 的 acrylic 在 layered 上不生效，
/// 所以背景必须自己画 —— 否则就是"完全没有毛玻璃效果"。
/// </summary>
internal static class OverlayPainter
{
    private const float PanelRadius = 13f;

    public static void Paint(
        Graphics g,
        OverlayFrame frame,
        OverlaySnapshot snapshot,
        OverlayPaintOptions options,
        OverlayFonts fonts,
        Bitmap? backdrop)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        Rectangle client = ToRect(frame.Client);
        float scale = (float)frame.Scale;
        if (client.Width < 8 || client.Height < 8) return;

        double effectiveScale = Math.Max(0.5, frame.Scale);

        if (options.Glass && backdrop is not null)
        {
            PaintGlassBackground(g, client, backdrop, effectiveScale);
        }
        else
        {
            using var solid = new SolidBrush(Color.FromArgb(0xF2, 9, 12, 16));
            g.FillRectangle(solid, client);
        }

        using (var edge = new Pen(Color.FromArgb(30, 255, 255, 255), Math.Max(1f, scale)))
        {
            g.DrawRectangle(edge, new Rectangle(client.X, client.Y, Math.Max(1, client.Width - 1),
                Math.Max(1, client.Height - 1)));
        }

        if (snapshot.Count == 0)
        {
            DrawEmpty(g, frame, options, fonts);
            return;
        }

        for (int i = 0; i < frame.Rows.Count && i < snapshot.Count; i++)
        {
            DrawRow(g, frame.Rows[i], snapshot.Devices[i], options, fonts, i > 0);
        }

        if (options.Interactive)
        {
            using var dashed = new Pen(Color.FromArgb(110, Color.FromArgb(OverlayTheme.Accent)), Math.Max(1f, scale))
            {
                DashStyle = DashStyle.Dot,
            };
            var inset = new RectangleF(client.X + 2.5f * scale, client.Y + 2.5f * scale,
                client.Width - 5f * scale, client.Height - 5f * scale);
            g.DrawRectangle(dashed, inset.X, inset.Y, inset.Width, inset.Height);
        }
    }

    /// <summary>
    /// 玻璃底：模糊桌面 → 深色叠色 → 顶部一道很淡的高光。
    /// 叠色刻意接近 Win11 深色 acrylic 的观感（能看见背后的形状，但文字始终清晰）。
    /// </summary>
    private static void PaintGlassBackground(Graphics g, Rectangle client, Bitmap backdrop, double scale)
    {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(backdrop, client);

        using (var tint = new SolidBrush(Color.FromArgb(0x74, 10, 13, 17)))
        {
            g.FillRectangle(tint, client);
        }

        // 系统卡片是"上略亮、下略沉"的，给它一点体积感
        using (var depth = new LinearGradientBrush(client,
                   Color.FromArgb(14, 255, 255, 255),
                   Color.FromArgb(20, 0, 0, 0),
                   LinearGradientMode.Vertical))
        {
            g.FillRectangle(depth, client);
        }

        using var highlight = new Pen(Color.FromArgb(22, 255, 255, 255), Math.Max(1f, (float)scale));
        float inset = (float)Math.Min(client.Width, client.Height) * 0.06f;
        g.DrawLine(highlight, client.X + inset, client.Y + 1f, client.Right - inset, client.Y + 1f);
        _ = PanelRadius;
    }

    // ────────────────────────── 一行设备 ──────────────────────────

    private static void DrawRow(
        Graphics g,
        RectI rowRect,
        in OverlayDevice device,
        OverlayPaintOptions options,
        OverlayFonts fonts,
        bool drawDivider)
    {
        Rectangle row = ToRect(rowRect);
        float scale = (float)options.Scale;
        if (row.Width < 24 || row.Height < 12) return;

        if (drawDivider)
        {
            using var line = new Pen(Color.FromArgb(OverlayTheme.WithAlpha(OverlayTheme.Divider, 26)), 1f);
            g.DrawLine(line, row.X, row.Y, row.Right, row.Y);
        }

        float diameter = Math.Clamp(
            Math.Min(row.Height - 12f * scale, (float)OverlayMetrics.MaxRingDiameter * scale),
            24f * scale, 58f * scale);

        float centerX = row.X + diameter / 2f + 2f * scale;
        float centerY = row.Y + row.Height / 2f;

        DrawRing(g, centerX, centerY, diameter, device, scale);

        // 环心数字：健康/充电用近白或薄荷绿，只有低电/危急才上暖色
        string percent = device.PercentText;
        float inner = diameter * 0.58f;
        int numberColor = device.HasReading
            ? NumberColorFor(device)
            : OverlayTheme.WithAlpha(OverlayTheme.TextDim, 220);

        DrawFitted(g, percent,
            new RectangleF(centerX - inner / 2f, centerY - inner / 2f, inner, inner),
            Math.Max(7f, diameter * 0.42f), 6f, numberColor,
            true, StringAlignment.Center, StringAlignment.Center, fonts);

        // 右边两行：设备名（第一视觉）/ 状态·档位（第二行左右分栏）
        float textX = centerX + diameter / 2f + 13f * scale;
        float textWidth = Math.Max(10f, row.Right - textX - 2f * scale);

        float blockHeight = row.Height * 0.66f;
        float nameHeight = blockHeight * 0.58f;
        float statusHeight = blockHeight * 0.42f;
        float top = centerY - blockHeight / 2f;

        DrawFitted(g, device.Model,
            new RectangleF(textX, top, textWidth, nameHeight),
            Math.Clamp(nameHeight * 0.98f, 7f, 13f * scale), 6.5f,
            OverlayTheme.WithAlpha(OverlayTheme.TextPrimary, device.HasReading ? 240 : 165),
            false, StringAlignment.Near, StringAlignment.Far, fonts);

        string status = device.StatusText;
        int statusColor = device.HasReading
            ? OverlayTheme.WithAlpha(OverlayTheme.ColorFor(device.Percent, device.Charging), device.Stale ? 175 : 215)
            : OverlayTheme.WithAlpha(OverlayTheme.TextDim, 205);

        float stageWidth = string.IsNullOrEmpty(device.StageText) ? 0f : 62f * scale;
        DrawFitted(g, status,
            new RectangleF(textX, top + nameHeight, Math.Max(10f, textWidth - stageWidth), statusHeight),
            Math.Clamp(statusHeight * 0.96f, 6f, 9.5f * scale), 6f,
            statusColor, false, StringAlignment.Near, StringAlignment.Near, fonts);

        if (stageWidth > 0)
        {
            DrawFitted(g, device.StageText!,
                new RectangleF(row.Right - stageWidth, top + nameHeight, stageWidth, statusHeight),
                Math.Clamp(statusHeight * 0.92f, 6f, 9f * scale), 6f,
                OverlayTheme.WithAlpha(OverlayTheme.TextSecondary, 205), false, StringAlignment.Far,
                StringAlignment.Near, fonts);
        }
    }

    /// <summary>健康/充电用近白或薄荷绿，只有低电/危急才上暖色 —— 平时整块面板几乎是灰阶的。</summary>
    private static int NumberColorFor(in OverlayDevice device)
    {
        int alpha = device.Stale ? 195 : 250;
        return OverlayTheme.AccentFor(device.Percent, device.Charging) switch
        {
            OverlayAccent.Critical => OverlayTheme.WithAlpha(OverlayTheme.Red, alpha),
            OverlayAccent.Low => OverlayTheme.WithAlpha(OverlayTheme.Amber, alpha),
            OverlayAccent.Charging => OverlayTheme.WithAlpha(OverlayTheme.Mint, alpha),
            _ => OverlayTheme.WithAlpha(OverlayTheme.TextPrimary, alpha),
        };
    }

    private static void DrawRing(
        Graphics g,
        float centerX,
        float centerY,
        float diameter,
        in OverlayDevice device,
        float scale)
    {
        var box = new RectangleF(centerX - diameter / 2f, centerY - diameter / 2f, diameter, diameter);
        float thickness = Math.Clamp(diameter * 0.10f, 2.2f, 5f);

        int trackColor = OverlayTheme.WithAlpha(OverlayTheme.Track, device.HasReading ? 150 : 95);
        using (var track = new Pen(Color.FromArgb(trackColor), thickness)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
               })
        {
            g.DrawArc(track, box, (float)OverlayTheme.RingStartAngle, (float)OverlayTheme.RingSweepAngle);
        }

        if (!device.HasReading) return;

        double sweep = OverlayTheme.RingSweep(device.Percent);
        if (sweep <= 0) return;

        int accent = OverlayTheme.ColorFor(device.Percent, device.Charging);
        int alpha = device.Stale ? 155 : 240;

        using (var arc = new Pen(Color.FromArgb(OverlayTheme.WithAlpha(accent, alpha)), thickness)
               {
                   StartCap = LineCap.Round,
                   EndCap = LineCap.Round,
               })
        {
            g.DrawArc(arc, box, (float)OverlayTheme.RingStartAngle, (float)sweep);
        }

        // 充电：弧线末端一个小实心点，比闪电图标安静
        if (device.Charging && sweep > 4)
        {
            double endAngle = (OverlayTheme.RingStartAngle + sweep) * Math.PI / 180.0;
            float radius = diameter / 2f;
            float px = centerX + (float)Math.Cos(endAngle) * radius;
            float py = centerY + (float)Math.Sin(endAngle) * radius;
            float dot = Math.Max(1.4f, thickness * 0.85f);

            using var brush = new SolidBrush(Color.FromArgb(OverlayTheme.WithAlpha(accent, 245)));
            g.FillEllipse(brush, px - dot, py - dot, dot * 2, dot * 2);
        }
    }

    // ────────────────────────── 空状态 ──────────────────────────

    private static void DrawEmpty(Graphics g, OverlayFrame frame, OverlayPaintOptions options, OverlayFonts fonts)
    {
        Rectangle content = ToRect(frame.Content);
        float scale = (float)options.Scale;
        if (content.Width < 40 || content.Height < 18) return;

        float diameter = Math.Clamp(content.Height * 0.46f, 20f, 42f * scale);
        var box = new RectangleF(
            content.X + content.Width * 0.26f - diameter / 2f,
            content.Y + content.Height / 2f - diameter / 2f,
            diameter, diameter);

        using (var track = new Pen(Color.FromArgb(OverlayTheme.WithAlpha(OverlayTheme.Track, 130)),
                   Math.Clamp(diameter * 0.07f, 1.6f, 4f))
               {
                   DashStyle = DashStyle.Dot,
               })
        {
            g.DrawArc(track, box, (float)OverlayTheme.RingStartAngle, (float)OverlayTheme.RingSweepAngle);
        }

        float textX = content.X + content.Width * 0.26f + diameter / 2f + 12f * scale;
        float textWidth = Math.Max(10f, content.Right - textX);

        DrawFitted(g, "NO DEVICE",
            new RectangleF(textX, content.Y + content.Height * 0.18f, textWidth, content.Height * 0.34f),
            Math.Clamp(content.Height * 0.22f, 8f, 12f * scale), 7f,
            OverlayTheme.WithAlpha(OverlayTheme.TextSecondary, 220), false,
            StringAlignment.Near, StringAlignment.Center, fonts);

        DrawFitted(g, "WAITING FOR LINK",
            new RectangleF(textX, content.Y + content.Height * 0.54f, textWidth, content.Height * 0.28f),
            Math.Clamp(content.Height * 0.15f, 6f, 8.5f * scale), 6f,
            OverlayTheme.WithAlpha(OverlayTheme.TextDim, 220), false,
            StringAlignment.Near, StringAlignment.Center, fonts);
    }

    // ────────────────────────── 基元 ──────────────────────────

    private static Rectangle ToRect(RectI rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>把文字塞进指定框：从 maxPx 往下降，直到量出来放得下（用户会拖窗口大小，字号不能写死）。</summary>
    private static void DrawFitted(
        Graphics g,
        string text,
        RectangleF box,
        float maxPixelSize,
        float minPixelSize,
        int argb,
        bool bold,
        StringAlignment horizontal,
        StringAlignment vertical,
        OverlayFonts fonts)
    {
        if (string.IsNullOrEmpty(text) || box.Width < 3 || box.Height < 3) return;

        float size = Math.Max(minPixelSize, maxPixelSize);
        Font font = fonts.Get(size, bold);
        SizeF measured = g.MeasureString(text, font);

        int guard = 0;
        while ((measured.Width > box.Width || measured.Height > box.Height) && size > minPixelSize && guard++ < 24)
        {
            size = Math.Max(minPixelSize, size * 0.9f);
            font = fonts.Get(size, bold);
            measured = g.MeasureString(text, font);
        }

        using var brush = new SolidBrush(Color.FromArgb(argb));
        using var format = new StringFormat(StringFormatFlags.NoWrap)
        {
            Alignment = horizontal,
            LineAlignment = vertical,
            Trimming = StringTrimming.EllipsisCharacter,
        };

        g.DrawString(text, font, brush, box, format);
    }
}
