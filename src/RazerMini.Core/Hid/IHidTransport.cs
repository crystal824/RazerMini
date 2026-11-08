namespace RazerMini.Core.Hid;

/// <summary>
/// 与具体操作系统解耦的 HID 通道。三种报文都要支持，因为不同品牌走的路子不同：
/// 雷蛇走 feature report、雷柏走 output + input report、漫步者只被动推 input report。
/// 平台实现（Windows 的 HidD_* / WriteFile）在 RazerMini 项目里；单元测试里换成假实现，
/// 就能在 Linux 上验证协议字节与时序。
/// </summary>
public interface IHidTransport : IDisposable
{
    HidDeviceInfo Info { get; }

    /// <summary>句柄是否可写（能发 SET_FEATURE / WriteFile）。</summary>
    bool CanWrite { get; }

    /// <summary>
    /// 读 feature report。<paramref name="buffer"/> 长度必须等于 <see cref="HidDeviceInfo.FeatureReportLength"/>，
    /// 且第一字节是 report ID。返回 false 时 <paramref name="win32Error"/> 是 GetLastError。
    /// </summary>
    bool TryGetFeature(byte[] buffer, out int win32Error);

    /// <summary>写 feature report。缓冲区约定同 <see cref="TryGetFeature"/>。</summary>
    bool TrySetFeature(byte[] buffer, out int win32Error);

    /// <summary>
    /// 写 output report（雷柏一代协议用）。<paramref name="buffer"/> 首字节是 report ID，
    /// 长度必须等于 <see cref="HidDeviceInfo.OutputReportLength"/>。
    /// </summary>
    bool TryWriteOutputReport(byte[] buffer, out int win32Error);

    /// <summary>
    /// 取**最近一条** input report（不是阻塞等待下一条）。
    /// 雷柏用它轮询应答（先 busy 后 OK），漫步者用它抓被动状态帧。
    /// 缓冲区首字节要填 report ID；返回的长度以实际写入为准。
    /// </summary>
    bool TryGetInputReport(byte[] buffer, out int bytesRead, out int win32Error);
}
