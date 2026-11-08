namespace RazerMini.Core.Overlay;

/// <summary>电量对应的强调色档位（环形电量、数字、状态字都用它）。</summary>
public enum OverlayAccent
{
    Neutral = 0,
    Charging,
    Healthy,
    Low,
    Critical,
}

/// <summary>
/// 配色。思路是"深色玻璃 + 一点冷青"：强调色只出现在电量本身和状态字上，
/// 面板与分隔线一律低对比 —— 极简靠的是留白和克制的颜色，不是堆装饰。
/// 取值都是 ARGB，渲染层直接 <c>Color.FromArgb(int)</c>；放在 Core 里是为了能被断言。
/// </summary>
public static class OverlayTheme
{
    // ── 前景 ──
    public const int TextPrimary = unchecked((int)0xFFEAF5F9);
    public const int TextSecondary = unchecked((int)0xFF97ADB8);
    public const int TextDim = unchecked((int)0xFF5F7681);
    public const int Accent = unchecked((int)0xFF86E2F5);      // 冷青（正常）
    public const int Mint = unchecked((int)0xFF7BEFC6);        // 充电
    public const int Amber = unchecked((int)0xFFF2B45C);       // 低电量
    public const int Red = unchecked((int)0xFFFF7A7A);         // 危急

    // ── 面板 ──
    public const int PanelBase = unchecked((int)0xFF090C10);
    public const int Divider = unchecked((int)0xFF9FC6D6);     // 只用它的低 alpha
    public const int Track = unchecked((int)0xFF2B3C46);

    /// <summary>环形总扫过角度：300°，缺口朝下，读数从右下往左下长。</summary>
    public const double RingSweepAngle = 300.0;

    /// <summary>环形起始角（GDI+ 口径：0°=3 点方向，顺时针为正）。</summary>
    public const double RingStartAngle = 120.0;

    public static int WithAlpha(int argb, int alpha)
    {
        int clamped = Math.Clamp(alpha, 0, 255);
        return (clamped << 24) | (argb & 0x00FFFFFF);
    }

    public static OverlayAccent AccentFor(int? percent, bool charging)
    {
        if (charging) return OverlayAccent.Charging;
        if (percent is not int value) return OverlayAccent.Neutral;
        int clamped = Math.Clamp(value, 0, 100);
        if (clamped <= 10) return OverlayAccent.Critical;
        if (clamped <= 25) return OverlayAccent.Low;
        return OverlayAccent.Healthy;
    }

    public static int ColorFor(OverlayAccent accent) => accent switch
    {
        OverlayAccent.Charging => Mint,
        OverlayAccent.Healthy => Accent,
        OverlayAccent.Low => Amber,
        OverlayAccent.Critical => Red,
        _ => TextDim,
    };

    public static int ColorFor(int? percent, bool charging) => ColorFor(AccentFor(percent, charging));

    /// <summary>
    /// 环形该画多长的弧。0% 不画；只要有读数就至少给一点弧长 ——
    /// 否则"1%"和"没读数"在视觉上完全一样，而这个项目最忌讳把"没读到"画成"0"。
    /// </summary>
    public static double RingSweep(int? percent)
    {
        if (percent is not int value) return 0;
        int clamped = Math.Clamp(value, 0, 100);
        if (clamped == 0) return 0;
        return Math.Max(RingSweepAngle * clamped / 100.0, 2.0);
    }
}
