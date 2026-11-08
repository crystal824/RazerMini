using System.Collections.Concurrent;
using RazerMini.Interop;

namespace RazerMini.Platform;

/// <summary>
/// 最小 UI 派发器：一个 message-only 窗口 + 自建消息（WM_APP+1）。
/// 轮询在后台线程跑，结果通过 <see cref="Post"/> 回到 UI 线程，避免卡菜单。
/// </summary>
internal sealed class Dispatcher : NativeWindow
{
    private const uint WmAppRefresh = 0x8000 + 1;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly ConcurrentQueue<Action> _queue = new();

    public Dispatcher()
    {
        CreateHandle(new CreateParams
        {
            Caption = "RazerMiniDispatcher",
            Parent = HwndMessage,
            Style = 0,
            ExStyle = 0,
            X = 0,
            Y = 0,
            Width = 0,
            Height = 0,
        });
    }

    /// <summary>可以从任意线程调用。</summary>
    public void Post(Action action)
    {
        _queue.Enqueue(action);
        HidNative.PostMessage(Handle, WmAppRefresh, IntPtr.Zero, IntPtr.Zero);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == unchecked((int)WmAppRefresh))
        {
            while (_queue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    // UI 回调异常不能冒到消息循环，否则托盘会静默消失
                    Console.Error.WriteLine($"[RazerMini] UI 回调异常: {ex}");
                }
            }
            return;
        }

        base.WndProc(ref m);
    }
}
