namespace RazerMini.Core.Hid;

/// <summary>
/// 可选的"阻塞读一条 input report"能力。不是每个通道都能做到（<see cref="IHidTransport"/> 只保证
/// "取最近一条"），所以单独成接口，驱动用 <c>is IInterruptReader</c> 探测。
///
/// 为什么需要它：<see cref="IHidTransport.TryGetInputReport"/> 在 Windows 上对应
/// <c>HidD_GetInputReport</c>（IOCTL_HID_GET_INPUT_REPORT），只有部分设备的驱动会填"最后一条"缓存。
/// 漫步者 G3M Pro 的厂商集合就不填 —— 实测每次都回 Win32=31，但官方 App 明明能读到（它用挂起的
/// interrupt IN 读）。这条路就是给这类设备准备的：Windows 上用 overlapped <c>ReadFile</c>。
/// </summary>
public interface IInterruptReader
{
    /// <summary>
    /// 等到一条 input report（或超时）。缓冲区首字节填 report ID，长度需 ≥ InputReportLength。
    /// 返回 false 时 <paramref name="win32Error"/> 是 GetLastError（1460 = 超时，31 = 设备故障）。
    /// </summary>
    bool TryReadInputReport(byte[] buffer, int timeoutMs, out int bytesRead, out int win32Error);
}
