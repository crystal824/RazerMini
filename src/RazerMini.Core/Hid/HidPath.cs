namespace RazerMini.Core.Hid;

/// <summary>
/// HID 设备接口路径解析。**两种形态都要认**（2026-11-02 真机踩到，Switch Pro 因此被漏掉）：
///
/// <code>
/// USB  ：\\?\hid#vid_1532&amp;pid_007b&amp;mi_02#7&amp;2f0e4b3c&amp;0&amp;0000#{4d1e55b2-…}
/// 蓝牙 ：\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&amp;0002057e&amp;pid&amp;2009#…
/// </code>
///
/// 蓝牙那份是「4 位前缀（0002）+ VID」拼成的 8 位十六进制串，分隔符是 <c>&amp;</c> 而不是 <c>_</c>。
/// 早期实现只按 <c>"vid_xxxx"</c> 字面匹配路径来预过滤，于是 Switch Pro（057E，走蓝牙）在枚举阶段
/// 就被丢掉，诊断里表现为「枚举不到 057E 接口 / 手柄没连上蓝牙」——其实是路径压根没解析出来。
///
/// 不做"猜哪个字段是 VID"这种事：只认 <c>vid_</c> / <c>vid&amp;</c> 两种官方形态，取紧随其后的
/// 十六进制串；长度超过 4 位时取**低 4 位**（蓝牙会多出 0002 前缀）。
/// </summary>
public static class HidPath
{
    /// <summary>从接口路径解析 VID / PID；解析不出的位置返回 0。</summary>
    public static (ushort Vid, ushort Pid) Parse(string path) =>
        (ParseHexToken(path, "vid_") ?? ParseHexToken(path, "vid&") ?? 0,
         ParseHexToken(path, "pid_") ?? ParseHexToken(path, "pid&") ?? 0);

    public static ushort VendorId(string path) => Parse(path).Vid;

    public static ushort ProductId(string path) => Parse(path).Pid;

    /// <summary>路径是否属于某个厂商（枚举预过滤用，省掉对无关接口的 CreateFile）。</summary>
    public static bool MatchesVendor(string path, ushort vendorId) => Parse(path).Vid == vendorId;

    private static ushort? ParseHexToken(string path, string marker)
    {
        int i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        int start = i + marker.Length;
        int len = 0;
        while (start + len < path.Length && Uri.IsHexDigit(path[start + len])) len++;
        if (len < 4) return null;

        return ushort.TryParse(
            path.AsSpan(start + len - 4, 4),
            System.Globalization.NumberStyles.HexNumber,
            null,
            out ushort value)
            ? value
            : null;
    }
}
