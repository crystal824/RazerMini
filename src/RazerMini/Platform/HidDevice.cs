using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RazerMini.Core.Hid;
using RazerMini.Core.Logging;
using RazerMini.Interop;

namespace RazerMini.Platform;

/// <summary>
/// Windows 上的 <see cref="IHidTransport"/> 实现：HidD_GetFeature / HidD_SetFeature 直通。
///
/// 三个必须守住的约定（技能"通用工程坑"）：
/// 1. 缓冲区第一字节必须是 report ID（雷蛇是 0x00）；
/// 2. 缓冲区长度必须精确等于 FeatureReportByteLength，长了短了都报 Win32 87；
/// 3. 拔出后句柄失效要从错误码识别（6/1167），由上层重开。
///
/// 访问模式：雷蛇的 91 字节 feature 通道挂在 MI_00 的**鼠标集合**上，读写/只读常被
/// 鼠标驱动拒掉（Win32=5）。但 HID 的 feature IOCTL 是 FILE_ANY_ACCESS，用零权限句柄
/// （CreateFile access=0）往往照样能收发 feature report —— 所以第三种模式必须试。
/// </summary>
internal sealed class HidDevice : IHidTransport, IInterruptReader
{
    private const string ReadMethodPolling = "HidD_GetInputReport（取最近一条）";
    private const string ReadMethodInterrupt = "interrupt ReadFile（overlapped）";

    private readonly SafeFileHandle _handle;
    private readonly bool _canWrite;
    private readonly object _asyncReadLock = new();
    private SafeFileHandle? _asyncReadHandle;
    private IntPtr _readEvent;
    private bool _asyncReadUnavailable;
    private bool _disposed;

    private HidDevice(HidDeviceInfo info, SafeFileHandle handle, string accessMode, bool canWrite)
    {
        Info = info;
        _handle = handle;
        AccessMode = accessMode;
        _canWrite = canWrite;
    }

    public HidDeviceInfo Info { get; }

    /// <summary>成功打开时用的访问模式（读写 / 只读 / 零权限）。</summary>
    public string AccessMode { get; }

    /// <summary>零权限/只读句柄下写 feature 会失败，调用方据此提前给出可读原因。</summary>
    public bool CanWrite => _canWrite;

    public static (uint Access, string Name)[] AccessModes => new[]
    {
        (HidNative.GenericRead | HidNative.GenericWrite, "读写"),
        (HidNative.GenericRead, "只读"),
        (0u, "零权限"),
    };

    /// <summary>按 读写 → 只读 → 零权限 依次尝试。</summary>
    public static HidDevice? TryOpen(HidDeviceInfo info, ILog log)
    {
        int lastError = 0;
        foreach ((uint access, string name) in AccessModes)
        {
            HidDevice? device = TryOpenWith(access, name, info, log, out int error);
            if (device is not null)
            {
                return device;
            }
            lastError = error;
        }

        log.Error($"无法打开设备 {info}（三种访问模式都失败，最后一次 Win32={lastError}）。" +
                  "若为 5/32，通常是雷蛇 Synapse 或系统鼠标驱动独占");
        return null;
    }

    /// <summary>用指定访问模式打开，供诊断逐模式实测。</summary>
    public static HidDevice? TryOpenWith(uint access, string modeName, HidDeviceInfo info, ILog log, out int error)
    {
        SafeFileHandle handle = HidNative.CreateFile(info.Path, access,
            HidNative.FileShareRead | HidNative.FileShareWrite,
            IntPtr.Zero, HidNative.OpenExisting, 0, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            log.Warn($"CreateFile 失败（access=0x{access:X}，{modeName}）Win32 错误 {error}：{info.Path}");
            return null;
        }

        bool canWrite = (access & HidNative.GenericWrite) != 0;
        log.Debug($"已打开 {info.VendorId:X4}:{info.ProductId:X4}（{modeName}）：{info.Path}");
        if (!canWrite)
        {
            log.Warn($"{modeName}句柄：读电量可以试，写 DPI 可能会被拒（feature SET 需要写权限）");
        }

        error = 0;
        return new HidDevice(info, handle, modeName, canWrite);
    }

    public bool TryGetFeature(byte[] buffer, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length != Info.FeatureReportLength)
        {
            win32Error = 87;
            return false;
        }

        bool ok = HidNative.HidD_GetFeature(_handle, buffer, buffer.Length);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    public bool TrySetFeature(byte[] buffer, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.Length != Info.FeatureReportLength)
        {
            win32Error = 87;
            return false;
        }

        bool ok = HidNative.HidD_SetFeature(_handle, buffer, buffer.Length);
        win32Error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>
    /// 写 output report（雷柏/Switch Pro 用）。长度必须精确等于 OutputReportByteLength。
    ///
    /// 两条通路：句柄可写时走 <c>WriteFile</c>；**不可写（只读/零权限）时退回 <c>HidD_SetOutputReport</c>** ——
    /// HID 的这个 IOCTL 是 <c>FILE_ANY_ACCESS</c>，零权限句柄照样能发（和 feature report 同理）。
    /// 这一点很关键：Switch Pro 的厂商集合常被系统/Steam 占用，只能拿到零权限句柄。
    /// </summary>
    public bool TryWriteOutputReport(byte[] buffer, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.Length != Info.OutputReportLength)
        {
            win32Error = 87;
            return false;
        }

        if (_canWrite)
        {
            bool ok = HidNative.WriteFile(_handle, buffer, buffer.Length, out _, IntPtr.Zero);
            if (ok)
            {
                win32Error = 0;
                return true;
            }

            win32Error = Marshal.GetLastWin32Error();
            if (win32Error != 5) return false;
            // 被拒（5）→ 落下去试 HidD_SetOutputReport
        }

        bool direct = HidNative.HidD_SetOutputReport(_handle, buffer, buffer.Length);
        win32Error = direct ? 0 : Marshal.GetLastWin32Error();
        if (!direct && win32Error == 0) win32Error = 5;
        return direct;
    }

    /// <summary>取最近一条 input report（漫步者状态帧 / 雷柏应答）。</summary>
    public bool TryGetInputReport(byte[] buffer, out int bytesRead, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bytesRead = 0;

        if (buffer.Length < Info.InputReportLength)
        {
            win32Error = 87;
            return false;
        }

        bool ok = HidNative.HidD_GetInputReport(_handle, buffer, Info.InputReportLength);
        if (ok)
        {
            bytesRead = Info.InputReportLength;
            win32Error = 0;
            return true;
        }

        win32Error = Marshal.GetLastWin32Error();
        return false;
    }

    /// <summary>
    /// 阻塞读一条 input report（overlapped <c>ReadFile</c>）—— interrupt IN 端点的正道。
    ///
    /// 用一个**另开的只读句柄**：混合使用 overlapped / 非 overlapped 的 I/O 在同一个句柄上是非法的，
    /// 而主句柄还要供 feature / output 用，所以另开一个带 FILE_FLAG_OVERLAPPED 的读句柄。
    /// 开不出来（共享冲突等）就把这条路径标记为不可用，调用方退回轮询。
    /// </summary>
    public bool TryReadInputReport(byte[] buffer, int timeoutMs, out int bytesRead, out int win32Error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        bytesRead = 0;

        if (buffer.Length < Info.InputReportLength)
        {
            win32Error = 87;
            return false;
        }

        lock (_asyncReadLock)
        {
            if (!EnsureAsyncReadHandle(out win32Error))
            {
                return false;
            }

            HidNative.ResetEvent(_readEvent);
            var overlapped = new HidNative.NativeOverlapped { hEvent = _readEvent };

            bool issued = HidNative.ReadFile(_asyncReadHandle!, buffer, Info.InputReportLength,
                IntPtr.Zero, ref overlapped);
            if (!issued)
            {
                int error = Marshal.GetLastWin32Error();
                if (error != HidNative.ErrorIoPending)
                {
                    win32Error = error;
                    return false;
                }

                uint wait = HidNative.WaitForSingleObject(_readEvent, (uint)Math.Max(timeoutMs, 0));
                if (wait != HidNative.WaitObject0)
                {
                    // 超时：取消挂起的读，并等它真正收尾，否则 OVERLAPPED 不能复用
                    HidNative.CancelIoEx(_asyncReadHandle!, ref overlapped);
                    HidNative.GetOverlappedResult(_asyncReadHandle!, ref overlapped, out _, true);
                    win32Error = HidNative.ErrorTimeout;
                    return false;
                }
            }

            if (!HidNative.GetOverlappedResult(_asyncReadHandle!, ref overlapped, out uint transferred, false))
            {
                win32Error = Marshal.GetLastWin32Error();
                return false;
            }

            bytesRead = (int)transferred;
            win32Error = 0;
            return transferred > 0;
        }
    }

    /// <summary>这条通道上哪条读路径可用（诊断用）。</summary>
    public string ReadMethod
    {
        get
        {
            lock (_asyncReadLock)
            {
                if (_asyncReadUnavailable) return ReadMethodPolling;
                if (_asyncReadHandle is not null) return ReadMethodInterrupt;
            }

            return "未确定（按需尝试 " + ReadMethodInterrupt + "）";
        }
    }

    private bool EnsureAsyncReadHandle(out int error)
    {
        error = 0;
        if (_asyncReadHandle is not null && !_asyncReadHandle.IsInvalid) return true;
        if (_asyncReadUnavailable)
        {
            error = 1168;
            return false;
        }

        SafeFileHandle handle = HidNative.CreateFile(Info.Path, HidNative.GenericRead,
            HidNative.FileShareRead | HidNative.FileShareWrite, IntPtr.Zero, HidNative.OpenExisting,
            HidNative.FileFlagOverlapped, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            _asyncReadUnavailable = true;
            return false;
        }

        _readEvent = HidNative.CreateEventW(IntPtr.Zero, true, false, IntPtr.Zero);
        if (_readEvent == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            handle.Dispose();
            _asyncReadUnavailable = true;
            return false;
        }

        _asyncReadHandle = handle;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_asyncReadLock)
        {
            _asyncReadHandle?.Dispose();
            _asyncReadHandle = null;

            if (_readEvent != IntPtr.Zero)
            {
                HidNative.CloseHandle(_readEvent);
                _readEvent = IntPtr.Zero;
            }
        }

        _handle.Dispose();
    }
}
