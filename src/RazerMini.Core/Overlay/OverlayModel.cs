using System.Text;

namespace RazerMini.Core.Overlay;

/// <summary>
/// 桌面浮窗里的一台设备。渲染层不再回头查任何表 —— 名称、电量、充电、是否陈旧全在这里，
/// 这样浮窗的绘制完全不依赖 TrayApp 的内部状态（也就不会在 UI 线程上摸到正在被轮询线程改的表）。
/// </summary>
public readonly record struct OverlayDevice(
    string Key,
    string Family,
    string Model,
    int? Percent,
    bool Charging,
    bool Stale,
    string? StageText)
{
    public bool HasReading => Percent is not null;

    /// <summary>大字号数字。没读到就是 <c>--</c>，绝不把"没读到"画成 0。</summary>
    public string PercentText => Percent is null ? "--" : Math.Clamp(Percent.Value, 0, 100).ToString();

    /// <summary>
    /// 窗格上的状态字。刻意用全大写英文：窗格很窄，小字号中文会糊成一团，
    /// 而"读数是否新鲜"必须一眼能分辨（这正是本项目最容易被误解的地方）。
    /// 用词也刻意短：<c>LAST KNOWN</c> 这类长词在窄窗格里会被省略号吃掉（预览时实测过）。
    /// </summary>
    public string StatusText =>
        !HasReading ? (Stale ? "NO RESP" : "NO LINK")
        : Charging ? "CHARGING"
        : Stale ? "STALE"
        : "LINK";
}

/// <summary>某一时刻浮窗要显示的全部内容。不可变快照，跨线程传递安全。</summary>
public sealed class OverlaySnapshot
{
    public static readonly OverlaySnapshot Empty =
        new(Array.Empty<OverlayDevice>(), DateTimeOffset.MinValue);

    public OverlaySnapshot(IReadOnlyList<OverlayDevice> devices, DateTimeOffset timestamp)
    {
        Devices = devices ?? Array.Empty<OverlayDevice>();
        Timestamp = timestamp;
    }

    public IReadOnlyList<OverlayDevice> Devices { get; }
    public DateTimeOffset Timestamp { get; }
    public int Count => Devices.Count;

    /// <summary>设备集合签名。只用来判断"有没有新设备接入、要不要播入场动画"。</summary>
    public string KeySignature => string.Join("|", Devices.Select(d => d.Key));
}

/// <summary>窗格文本的纯函数处理（可单测）。</summary>
public static class OverlayText
{
    public const int MaxLabelLength = 18;

    /// <summary>
    /// 设备名 → 窗格标签：去掉括号里的补充说明（"（无线接收器）"）、转大写、压缩空白、超长截断。
    /// 窗格只承担"认出是哪只设备"，完整名称仍在托盘菜单里。
    /// </summary>
    public static string ShortLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "DEVICE";

        var builder = new StringBuilder(text.Length);
        int depth = 0;
        foreach (char c in text)
        {
            if (c is '（' or '(' or '【' or '[')
            {
                depth++;
                continue;
            }
            if (c is '）' or ')' or '】' or ']')
            {
                if (depth > 0) depth--;
                continue;
            }
            if (depth > 0) continue;
            builder.Append(c);
        }

        string[] parts = builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string cleaned = string.Join(' ', parts).ToUpperInvariant();
        if (cleaned.Length > MaxLabelLength)
        {
            cleaned = cleaned[..(MaxLabelLength - 1)].TrimEnd() + "…";
        }

        return cleaned.Length == 0 ? "DEVICE" : cleaned;
    }

    /// <summary>
    /// 档位制设备的说明，**纯 ASCII**（如 <c>STAGE 4/5</c>）。窗格用等宽字体（Consolas），
    /// 它没有中文字形，GDI+ 也不会自动回退到中文字体 —— 塞中文进去就是一堆方块。
    /// 中文版（"满 5/5 档"）留给托盘菜单。
    /// </summary>
    public static string? StageLabel(int? level, int? count)
    {
        if (level is not int index || count is not int total || total <= 0) return null;
        int shown = Math.Clamp(index + 1, 1, total);
        return $"STAGE {shown}/{total}";
    }
}
