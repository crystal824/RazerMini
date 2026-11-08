namespace RazerMini.Core.Overlay;

/// <summary>整数矩形（客户区坐标）。浮窗布局全部用整数像素，避免半像素导致的 1px 毛边。</summary>
public readonly record struct RectI(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int px, int py) => px >= X && py >= Y && px < Right && py < Bottom;

    public RectI Inflate(int dx, int dy) => new(X - dx, Y - dy, Width + dx * 2, Height + dy * 2);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>鼠标落在窗体的哪个区域。只有"交互打开"时才有意义。</summary>
public enum OverlayZone
{
    Outside = 0,
    Body,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>一次布局的结果：标题行 + 每台设备一行。</summary>
public sealed class OverlayFrame
{
    public OverlayFrame(
        RectI client,
        RectI header,
        RectI content,
        IReadOnlyList<RectI> rows,
        double scale,
        bool showHeader)
    {
        Client = client;
        Header = header;
        Content = content;
        Rows = rows;
        Scale = scale;
        ShowHeader = showHeader;
    }

    public RectI Client { get; }
    public RectI Header { get; }
    public RectI Content { get; }

    /// <summary>每台设备一行，从上到下。</summary>
    public IReadOnlyList<RectI> Rows { get; }

    public double Scale { get; }
    public bool ShowHeader { get; }
    public int RowCount => Rows.Count;
}

/// <summary>
/// 浮窗几何：全部按 DPI 缩放后的像素计算，纯算术、无 GDI 依赖，所以能在 Linux 上跑断言。
/// Windows 层只负责按这套几何画。
/// </summary>
public static class OverlayMetrics
{
    public const double Padding = 14;
    public const double MinRowHeight = 54;
    public const double MinWindowWidth = 244;
    public const double MinWindowHeight = 92;
    public const double ResizeEdge = 8;
    public const double MaxRingDiameter = 58;
    public const double DefaultMargin = 32;

    /// <summary>逻辑尺寸 → 像素。缩放系数下限 0.5，防止异常 DPI 把界面压成 1px。</summary>
    public static int Scale(double value, double scale)
    {
        double factor = double.IsFinite(scale) && scale > 0.5 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(value * factor));
    }
}

public static class OverlayLayout
{
    /// <summary>
    /// 算每台设备一行的位置。**没有标题行**（用户明确要求去掉）：整块内边距区直接分行。
    /// 行永远铺满内容区，加设备/改窗口大小只重排，不留空洞、也不会被裁掉。
    /// </summary>
    public static OverlayFrame Compute(RectI client, int count, double scale)
    {
        int pad = OverlayMetrics.Scale(OverlayMetrics.Padding, scale);
        var inner = new RectI(
            client.X + pad,
            client.Y + pad,
            Math.Max(1, client.Width - pad * 2),
            Math.Max(1, client.Height - pad * 2));

        if (count <= 0)
        {
            return new OverlayFrame(client, default, inner, Array.Empty<RectI>(), scale, false);
        }

        var rows = new List<RectI>(count);
        for (int i = 0; i < count; i++)
        {
            int top = inner.Y + i * inner.Height / count;
            int bottom = inner.Y + (i + 1) * inner.Height / count;
            rows.Add(new RectI(inner.X, top, inner.Width, Math.Max(1, bottom - top)));
        }

        return new OverlayFrame(client, default, inner, rows, scale, false);
    }

    /// <summary>
    /// 最小窗口尺寸。随设备数量增长：每台设备至少要有 <see cref="OverlayMetrics.MinRowHeight"/> 的高度，
    /// 否则环形电量会被压成一条线。
    /// </summary>
    public static (int Width, int Height) MinSize(int count, double scale)
    {
        int pad = OverlayMetrics.Scale(OverlayMetrics.Padding, scale);
        int rows = Math.Max(1, count);
        int rowHeight = OverlayMetrics.Scale(OverlayMetrics.MinRowHeight, scale);

        int width = OverlayMetrics.Scale(OverlayMetrics.MinWindowWidth, scale);
        int height = pad * 2 + rows * rowHeight;

        return (width, Math.Max(OverlayMetrics.Scale(OverlayMetrics.MinWindowHeight, scale), height));
    }

    /// <summary>当前设备数量下"舒服"的默认高度：比最小高度略松，环形和文字之间有呼吸。</summary>
    public static int PreferredHeight(int count, double scale)
    {
        int pad = OverlayMetrics.Scale(OverlayMetrics.Padding, scale);
        int rows = Math.Max(1, count);
        int rowHeight = OverlayMetrics.Scale(OverlayMetrics.MinRowHeight * 1.22, scale);
        int height = pad * 2 + rows * rowHeight;

        var (_, minimum) = MinSize(count, scale);
        return Math.Max(minimum, height);
    }

    /// <summary>默认宽度：够放"环形 + 设备名 + 3 位数字"，不宽也不挤。</summary>
    public static int PreferredWidth(double scale) => OverlayMetrics.Scale(292, scale);

    /// <summary>命中区域。角优先于边，边宽随 DPI 缩放（高 DPI 下 4px 根本抓不住）。</summary>
    public static OverlayZone HitTest(RectI client, int x, int y, double scale)
    {
        if (!client.Contains(x, y)) return OverlayZone.Outside;

        int edge = Math.Clamp(OverlayMetrics.Scale(OverlayMetrics.ResizeEdge, scale), 4, 18);

        bool left = x - client.X < edge;
        bool right = client.Right - x <= edge;
        bool top = y - client.Y < edge;
        bool bottom = client.Bottom - y <= edge;

        if (left && top) return OverlayZone.TopLeft;
        if (right && top) return OverlayZone.TopRight;
        if (left && bottom) return OverlayZone.BottomLeft;
        if (right && bottom) return OverlayZone.BottomRight;
        if (left) return OverlayZone.Left;
        if (right) return OverlayZone.Right;
        if (top) return OverlayZone.Top;
        if (bottom) return OverlayZone.Bottom;

        return OverlayZone.Body;
    }

    /// <summary>按拖动/缩放的方向算新矩形。宽度高度不会低于最小值。</summary>
    public static RectI ApplyDrag(RectI start, OverlayZone zone, int deltaX, int deltaY, int minWidth, int minHeight)
    {
        int x = start.X, y = start.Y, width = start.Width, height = start.Height;

        bool movingLeft = zone is OverlayZone.Left or OverlayZone.TopLeft or OverlayZone.BottomLeft;
        bool movingRight = zone is OverlayZone.Right or OverlayZone.TopRight or OverlayZone.BottomRight;
        bool movingTop = zone is OverlayZone.Top or OverlayZone.TopLeft or OverlayZone.TopRight;
        bool movingBottom = zone is OverlayZone.Bottom or OverlayZone.BottomLeft or OverlayZone.BottomRight;

        if (zone == OverlayZone.Body)
        {
            return new RectI(start.X + deltaX, start.Y + deltaY, start.Width, start.Height);
        }

        if (movingLeft)
        {
            int newWidth = Math.Max(minWidth, start.Width - deltaX);
            x = start.Right - newWidth;
            width = newWidth;
        }
        else if (movingRight)
        {
            width = Math.Max(minWidth, start.Width + deltaX);
        }

        if (movingTop)
        {
            int newHeight = Math.Max(minHeight, start.Height - deltaY);
            y = start.Bottom - newHeight;
            height = newHeight;
        }
        else if (movingBottom)
        {
            height = Math.Max(minHeight, start.Height + deltaY);
        }

        return new RectI(x, y, width, height);
    }

    /// <summary>
    /// 把矩形压进屏幕工作区：位置固定靠它兜底（换分辨率/拔显示器后窗口不能跑到看不见的地方）。
    /// 只有在真的越界时才动，正常情况下一像素都不改。
    /// </summary>
    public static RectI ClampInto(RectI window, RectI work, int minWidth, int minHeight)
    {
        if (work.Width <= 0 || work.Height <= 0) return window;

        int width = Math.Clamp(window.Width, Math.Min(minWidth, work.Width), work.Width);
        int height = Math.Clamp(window.Height, Math.Min(minHeight, work.Height), work.Height);
        int x = Math.Clamp(window.X, work.X, Math.Max(work.X, work.Right - width));
        int y = Math.Clamp(window.Y, work.Y, Math.Max(work.Y, work.Bottom - height));
        return new RectI(x, y, width, height);
    }

    /// <summary>右上角默认位置：离边缘留 <see cref="OverlayMetrics.DefaultMargin"/>（按 DPI 缩放）。</summary>
    public static RectI DefaultBounds(RectI work, int width, int height, double scale, int minWidth, int minHeight)
    {
        int margin = OverlayMetrics.Scale(OverlayMetrics.DefaultMargin, scale);
        var rect = new RectI(work.Right - width - margin, work.Y + margin, width, height);
        return ClampInto(rect, work, minWidth, minHeight);
    }
}

/// <summary>
/// 浮窗位置/大小的持久化口径：存成"监视器工作区内的比例"，不是绝对像素。
/// 这样换分辨率、改缩放后仍能落回原处（绝对像素会跑到屏幕外）。
/// </summary>
public readonly record struct OverlayPlacement(
    string Monitor,
    double Left,
    double Top,
    double Width,
    double Height)
{
    public bool IsValid =>
        !string.IsNullOrEmpty(Monitor) &&
        Width > 0.01 && Height > 0.01 &&
        double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height);
}

public static class OverlayPlacementMath
{
    public static OverlayPlacement Capture(RectI window, RectI work, string monitor)
    {
        if (work.Width <= 0 || work.Height <= 0) return default;

        return new OverlayPlacement(
            monitor ?? string.Empty,
            Clamp01((window.X - work.X) / (double)work.Width),
            Clamp01((window.Y - work.Y) / (double)work.Height),
            Clamp01(window.Width / (double)work.Width),
            Clamp01(window.Height / (double)work.Height));
    }

    public static RectI Resolve(OverlayPlacement placement, RectI work, int minWidth, int minHeight)
    {
        if (work.Width <= 0 || work.Height <= 0) return default;

        if (!placement.IsValid)
        {
            // 没有/坏掉的位置记录：给"右上角留边距"的默认框，不要返回 0 尺寸
            int width = Math.Clamp(OverlayLayout.PreferredWidth(1.0),
                Math.Min(minWidth, work.Width), work.Width);
            int height = Math.Clamp(OverlayLayout.PreferredHeight(1, 1.0),
                Math.Min(minHeight, work.Height), work.Height);
            return OverlayLayout.DefaultBounds(work, width, height, 1.0, minWidth, minHeight);
        }

        int resolvedWidth = (int)Math.Round(placement.Width * work.Width);
        int resolvedHeight = (int)Math.Round(placement.Height * work.Height);
        var rect = new RectI(
            work.X + (int)Math.Round(placement.Left * work.Width),
            work.Y + (int)Math.Round(placement.Top * work.Height),
            resolvedWidth,
            resolvedHeight);

        return OverlayLayout.ClampInto(rect, work, minWidth, minHeight);
    }

    private static double Clamp01(double value) =>
        !double.IsFinite(value) ? 0 : Math.Clamp(value, 0, 1);
}
