using RazerMini.Core.Logging;
using RazerMini.Core.Overlay;
using RazerMini.Core.Settings;
using RazerMini.Interop;
using RazerMini.Platform;

namespace RazerMini.Ui;

/// <summary>
/// 桌面浮窗：Win11 系统卡片观感（模糊桌面 + 深色叠色 + 细描边），每台外设一行。
///
/// 设计约束（用户明确要求）：
/// <list type="bullet">
/// <item>**不置顶**：桌面挂件不能挡窗口；平时沉在 z-order 最底，打开交互时才提到普通窗口层最前。</item>
/// <item>默认**不可交互**：点击/滚轮完全穿透，位置锁定；交互开关在托盘菜单里。</item>
/// <item>打开交互后**只能用鼠标**拖动与拖边缘缩放，界面上没有任何按钮。</item>
/// <item>零动画、零常驻定时器：只有要显示的内容真的变了才重画。</item>
/// </list>
///
/// 玻璃的实现见 <see cref="ScreenBackdrop"/>：点击穿透要求窗口是 layered，
/// 而 layered 窗口上 DWM acrylic 不生效（真机"完全没有毛玻璃"），所以背景由我们自己
/// "截屏 → 强模糊 → 叠色"算出来。窗口本体是不透明的（layered + 常量 alpha 255），
/// 因此可以放心用双缓冲整块贴屏 —— 不会有刷新闪烁，也不会有 alpha 被冲掉的问题。
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WmEraseBackground = 0x0014;
    private const int WmDisplayChange = 0x007E;

    private readonly FileLog _log;
    private readonly AppSettings _settings;
    private readonly OverlayFonts _fonts = new();

    /// <summary>截屏+模糊有一定成本，改窗口大小的过程中先攒一下，停下来再抓一次。</summary>
    private readonly System.Windows.Forms.Timer _recaptureTimer = new() { Interval = 260 };

    private OverlaySnapshot _snapshot = OverlaySnapshot.Empty;
    private string _keySignature = string.Empty;
    private string _renderSignature = string.Empty;

    private Bitmap? _backdrop;
    private bool _glassEnabled;

    private bool _interactive;
    private string _glassMode = "unknown";

    private bool _pressing;
    private OverlayZone _zone = OverlayZone.Outside;
    private Point _pressCursor;
    private Rectangle _pressBounds;

    public OverlayForm(AppSettings settings, FileLog log)
    {
        _settings = settings;
        _log = log;

        // 窗口是"不透明"的（玻璃自己画），所以双缓冲安全，能彻底消除重绘闪烁
        SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint, true);
        DoubleBuffered = true;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;                 // 桌面挂件：不置顶，别挡住别人的窗口
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(9, 12, 16);
        Text = "RazerMini 浮窗";
        KeyPreview = false;
        MinimizeBox = false;
        MaximizeBox = false;
        ControlBox = false;
        AutoScaleMode = AutoScaleMode.None;

        _interactive = settings.OverlayInteractive;
        _glassEnabled = !string.Equals(settings.OverlayGlass, "solid", StringComparison.OrdinalIgnoreCase);
        _glassMode = _glassEnabled ? "自渲染毛玻璃" : "solid";

        var (minWidth, minHeight) = OverlayLayout.MinSize(1, 1.0);
        MinimumSize = new Size(minWidth, minHeight);
        Size = new Size(OverlayLayout.PreferredWidth(1.0), OverlayLayout.PreferredHeight(1, 1.0));

        _recaptureTimer.Tick += (_, _) =>
        {
            _recaptureTimer.Stop();
            CaptureBackdrop();
        };
    }

    public bool Interactive => _interactive;

    /// <summary>用户在浮窗上完成拖动/缩放后触发（托盘层用来记日志）。</summary>
    public event EventHandler? PlacementChanged;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW：不进 Alt-Tab、不进任务栏
            parameters.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE：永远不抢焦点
            return parameters;
        }
    }

    private double DpiScale => DeviceDpi / 96.0;

    // ────────────────────────── 生命周期 ──────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowEffects.ApplyBaseStyle(Handle);
        WindowEffects.ApplyRoundedCorners(Handle);
        WindowEffects.SetClickThrough(Handle, !_interactive);
        WindowEffects.SendToBottom(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // 拖到另一块不同缩放的显示器时按比例缩放整块面板；
        // 但如果用户**正在拖动**，让鼠标说了算（否则会和拖拽互相拉扯）。
        double ratio = e.DeviceDpiOld > 0 ? e.DeviceDpiNew / (double)e.DeviceDpiOld : 1.0;
        if (!_pressing && Math.Abs(ratio - 1.0) > 0.01 && IsHandleCreated && !IsDisposed)
        {
            try
            {
                var screen = Screen.FromRectangle(Bounds);
                var work = ToRectI(screen.WorkingArea);
                var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, e.DeviceDpiNew / 96.0);
                var scaled = new RectI(Left, Top,
                    (int)Math.Round(Width * ratio),
                    (int)Math.Round(Height * ratio));

                Bounds = ToRectangle(OverlayLayout.ClampInto(scaled, work, minWidth, minHeight));
                SavePlacement();
            }
            catch (Exception ex)
            {
                _log.Warn($"跨显示器缩放浮窗失败：{ex.Message}");
            }
        }

        ApplyMinimumSize();
        QueueRecapture();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (IsHandleCreated && Visible) QueueRecapture();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 背景由 OnPaint 整块画（含玻璃底），这里不做任何事，避免闪一下底色
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Size size = ClientSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var frame = OverlayLayout.Compute(new RectI(0, 0, size.Width, size.Height), _snapshot.Count, DpiScale);
        var options = new OverlayPaintOptions
        {
            Glass = _glassEnabled,
            Interactive = _interactive,
            Scale = DpiScale,
        };

        OverlayPainter.Paint(e.Graphics, frame, _snapshot, options, _fonts, _backdrop);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmEraseBackground)
        {
            m.Result = (IntPtr)1;   // 不擦背景：擦了就是一闪黑
            return;
        }

        if (m.Msg == WmDisplayChange)
        {
            base.WndProc(ref m);
            RecoverOntoScreen();
            return;
        }

        base.WndProc(ref m);
    }

    // ────────────────────────── 对外接口 ──────────────────────────

    public void ShowOverlay(bool show)
    {
        if (show)
        {
            if (!Visible)
            {
                ApplyPlacementFromSettings();
                Show();
            }

            ApplyZOrder();

            // 必须等窗口真的显示出来，截屏才抓得到它背后的桌面
            BeginInvoke(new Action(() =>
            {
                CaptureBackdrop();
                Invalidate();
            }));

            _log.Debug($"浮窗已显示：{Bounds}（玻璃={_glassMode}，交互={_interactive}，置顶=False）");
        }
        else
        {
            SavePlacement();
            Hide();
            _log.Info("浮窗已隐藏");
        }
    }

    /// <summary>喂一帧新数据。**内容没变就一个像素都不重画**。</summary>
    public void Apply(OverlaySnapshot snapshot)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() => Apply(snapshot)));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，这一帧丢掉即可
            }
            return;
        }

        _snapshot = snapshot;

        string keySignature = snapshot.KeySignature;
        bool devicesChanged = keySignature != _keySignature;
        _keySignature = keySignature;

        string renderSignature = BuildRenderSignature(snapshot);
        if (!devicesChanged && renderSignature == _renderSignature) return;   // 数值没变，不重绘
        _renderSignature = renderSignature;

        if (devicesChanged)
        {
            EnsureFits();
            _log.Debug($"浮窗内容更新：{snapshot.Count} 台设备（{keySignature}）");
        }

        // 背后的桌面可能已经换了（换壁纸/进出交互层），顺手重抓一次玻璃底
        if (!_pressing) CaptureBackdrop();

        Invalidate();
    }

    /// <summary>
    /// 是否值得重画。只比较"会画到界面上的东西"：设备集合、电量、充电、陈旧、档位。
    /// 时间戳/原始字节不参与 —— 否则每轮轮询都会闪一次。
    /// </summary>
    private static string BuildRenderSignature(OverlaySnapshot snapshot)
    {
        var parts = new List<string>(snapshot.Count + 1) { snapshot.Count.ToString() };
        foreach (OverlayDevice device in snapshot.Devices)
        {
            parts.Add($"{device.Key}|{device.Percent}|{device.Charging}|{device.Stale}|{device.StageText}|{device.Model}");
        }

        return string.Join(";", parts);
    }

    /// <summary>交互开关。关闭时立刻把位置落盘 —— "交互关闭时位置固定"。</summary>
    public void SetInteractive(bool interactive)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(() => SetInteractive(interactive)));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁
            }
            return;
        }

        if (_interactive == interactive) return;
        _interactive = interactive;

        Rectangle bounds = Bounds;
        WindowEffects.SetClickThrough(Handle, !interactive);
        if (Bounds != bounds) Bounds = bounds;

        ApplyZOrder();

        if (!interactive)
        {
            Cursor = Cursors.Default;
            SavePlacement();
        }

        // 层级变了，背后的东西也变了（提到前面时后面可能是某个窗口）
        CaptureBackdrop();
        Invalidate();

        _log.Info(interactive
            ? "浮窗交互已打开：提到上层、可拖动/缩放（界面本身没有按钮）"
            : "浮窗交互已关闭：点击穿透 + 沉到底层 + 位置锁定");
    }

    /// <summary>托盘菜单里改玻璃模式后调用（毛玻璃 ↔ 纯色）。</summary>
    public void RefreshChrome()
    {
        _glassEnabled = !string.Equals(_settings.OverlayGlass, "solid", StringComparison.OrdinalIgnoreCase);
        _glassMode = _glassEnabled ? "自渲染毛玻璃" : "solid";

        if (!_glassEnabled)
        {
            _backdrop?.Dispose();
            _backdrop = null;
        }
        else
        {
            CaptureBackdrop();
        }

        Invalidate();
    }

    public string DescribeState() =>
        $"浮窗 可见={Visible} 交互={_interactive} 置顶={TopMost} 玻璃={_glassMode} " +
        $"底图={( _backdrop is null ? "无" : $"{_backdrop.Width}x{_backdrop.Height}")} " +
        $"尺寸={Width}x{Height} 位置={Left},{Top} 设备={_snapshot.Count} 字体={_fonts.Family} " +
        $"样式={WindowEffects.DescribeStyle(IsHandleCreated ? Handle : IntPtr.Zero)}";

    // ────────────────────────── 玻璃底 ──────────────────────────

    private void QueueRecapture()
    {
        if (!Visible || !_glassEnabled) return;
        _recaptureTimer.Stop();
        _recaptureTimer.Start();
    }

    /// <summary>
    /// 抓一次"窗口背后的桌面"并模糊。截屏期间把自身调到 alpha=1（肉眼不可见），抓完恢复。
    /// 失败时退化为纯色面板，不影响可用性。
    /// </summary>
    private void CaptureBackdrop()
    {
        if (!IsHandleCreated || IsDisposed || !Visible || !_glassEnabled) return;

        Size size = ClientSize;
        if (size.Width < 8 || size.Height < 8) return;

        Bitmap? shot = ScreenBackdrop.Capture(Handle, Bounds);
        if (shot is null)
        {
            _glassMode = "截屏失败（纯色兜底）";
            if (_backdrop is null) _log.Warn("浮窗玻璃底截屏失败，退化为纯色面板");
            return;
        }

        _backdrop?.Dispose();
        _backdrop = shot;
        _glassMode = "自渲染毛玻璃";
    }

    // ────────────────────────── 层叠顺序 ──────────────────────────

    /// <summary>
    /// 平时沉到最底（桌面挂件不该挡别人窗口）；打开交互时提到普通窗口最前，方便抓住它。
    /// **都不是 TopMost**：全屏游戏/置顶窗口仍然盖得住它。
    /// </summary>
    private void ApplyZOrder()
    {
        if (!IsHandleCreated) return;

        if (_interactive) WindowEffects.BringToFront(Handle);
        else WindowEffects.SendToBottom(Handle);
    }

    // ────────────────────────── 位置 ──────────────────────────

    private void ApplyMinimumSize()
    {
        var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, DpiScale);
        MinimumSize = new Size(minWidth, minHeight);
    }

    private Screen ResolveScreen(string? monitorName)
    {
        if (!string.IsNullOrEmpty(monitorName))
        {
            Screen? match = Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, monitorName, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    private void ApplyPlacementFromSettings()
    {
        ApplyMinimumSize();

        // 版本号 <1 = 上一版写进来的坏位置（切交互时跳到 0,0 的残留），当作没存过
        bool hasPlacement = _settings.HasOverlayPlacement && _settings.OverlayPlacementVersion >= 1;
        if (_settings.HasOverlayPlacement && !hasPlacement)
        {
            _log.Info("浮窗位置记录来自旧版本（可能是坏的），重置到默认右上角");
        }

        OverlayPlacement placement = hasPlacement
            ? new OverlayPlacement(_settings.OverlayMonitor, _settings.OverlayLeft, _settings.OverlayTop,
                _settings.OverlayWidth, _settings.OverlayHeight)
            : default;

        Screen screen = ResolveScreen(_settings.OverlayMonitor);
        var work = ToRectI(screen.WorkingArea);
        var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, DpiScale);

        Rectangle bounds;
        if (placement.IsValid)
        {
            bounds = ToRectangle(OverlayPlacementMath.Resolve(placement, work, minWidth, minHeight));
        }
        else
        {
            // 首次出现：屏幕右上角，离边缘留 32px（按 DPI 缩放），不贴边
            int width = OverlayLayout.PreferredWidth(DpiScale);
            int height = OverlayLayout.PreferredHeight(Math.Max(1, _snapshot.Count), DpiScale);
            bounds = ToRectangle(OverlayLayout.DefaultBounds(work, width, height, DpiScale, minWidth, minHeight));
        }

        Bounds = bounds;
    }

    private void EnsureFits()
    {
        ApplyMinimumSize();
        if (!IsHandleCreated) return;

        var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, DpiScale);
        int required = OverlayLayout.PreferredHeight(_snapshot.Count, DpiScale);
        if (Height >= required) return;

        var work = ToRectI(Screen.FromRectangle(Bounds).WorkingArea);
        var grown = new Rectangle(Left, Top, Width, required);
        Bounds = ToRectangle(OverlayLayout.ClampInto(ToRectI(grown), work, minWidth, minHeight));
        _log.Info($"浮窗随设备数量增高到 {Math.Min(required, work.Height)}px（{_snapshot.Count} 台）");
    }

    /// <summary>
    /// 分辨率/显示器变化后：只要窗口中心还在某块屏幕内就不动它；
    /// 已经跑到"不存在的区域"（拔了外接屏）就回到主屏右上角。
    /// </summary>
    private void RecoverOntoScreen()
    {
        if (!IsHandleCreated || IsDisposed) return;

        Point center = new(Left + Width / 2, Top + Height / 2);
        bool visible = Screen.AllScreens.Any(s => s.WorkingArea.Contains(center));

        var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, DpiScale);
        Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        var work = ToRectI(primary.WorkingArea);

        Rectangle target;
        if (visible)
        {
            var screen = Screen.FromRectangle(Bounds);
            target = ToRectangle(OverlayLayout.ClampInto(ToRectI(Bounds), ToRectI(screen.WorkingArea),
                minWidth, minHeight));
        }
        else
        {
            int width = Width > 0 ? Width : OverlayLayout.PreferredWidth(DpiScale);
            int height = Height > 0 ? Height : OverlayLayout.PreferredHeight(Math.Max(1, _snapshot.Count), DpiScale);
            target = ToRectangle(OverlayLayout.DefaultBounds(work, width, height, DpiScale, minWidth, minHeight));
        }

        if (target != Bounds)
        {
            Bounds = target;
            _log.Info($"显示器变化后浮窗重新定位：{target}");
            SavePlacement();
            QueueRecapture();
        }
    }

    public void SavePlacement()
    {
        try
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

            Screen screen = Screen.FromRectangle(Bounds);
            var work = ToRectI(screen.WorkingArea);
            OverlayPlacement placement = OverlayPlacementMath.Capture(ToRectI(Bounds), work, screen.DeviceName);

            bool changed =
                !string.Equals(_settings.OverlayMonitor, placement.Monitor, StringComparison.Ordinal) ||
                Math.Abs(_settings.OverlayLeft - placement.Left) > 0.0005 ||
                Math.Abs(_settings.OverlayTop - placement.Top) > 0.0005 ||
                Math.Abs(_settings.OverlayWidth - placement.Width) > 0.0005 ||
                Math.Abs(_settings.OverlayHeight - placement.Height) > 0.0005;

            if (!changed) return;

            _settings.OverlayMonitor = placement.Monitor;
            _settings.OverlayLeft = placement.Left;
            _settings.OverlayTop = placement.Top;
            _settings.OverlayWidth = placement.Width;
            _settings.OverlayHeight = placement.Height;
            _settings.OverlayPlacementVersion = 1;
            _settings.Save(AppPaths.SettingsFile, _log);

            PlacementChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Warn($"保存浮窗位置失败：{ex.Message}");
        }
    }

    // ────────────────────────── 鼠标（只在交互打开时才有事件） ──────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_interactive || e.Button != MouseButtons.Left) return;

        _zone = OverlayLayout.HitTest(new RectI(0, 0, ClientSize.Width, ClientSize.Height), e.X, e.Y, DpiScale);
        if (_zone == OverlayZone.Outside) return;

        _pressing = true;
        _pressCursor = Cursor.Position;
        _pressBounds = Bounds;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_pressing)
        {
            var (minWidth, minHeight) = OverlayLayout.MinSize(_snapshot.Count, DpiScale);
            Point cursor = Cursor.Position;
            RectI moved = OverlayLayout.ApplyDrag(
                ToRectI(_pressBounds), _zone,
                cursor.X - _pressCursor.X, cursor.Y - _pressCursor.Y,
                minWidth, minHeight);

            var work = ToRectI(Screen.FromPoint(cursor).WorkingArea);
            Bounds = ToRectangle(OverlayLayout.ClampInto(moved, work, minWidth, minHeight));
            return;
        }

        if (_interactive)
        {
            OverlayZone zone = OverlayLayout.HitTest(
                new RectI(0, 0, ClientSize.Width, ClientSize.Height), e.X, e.Y, DpiScale);
            Cursor = CursorFor(zone);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressing) return;

        _pressing = false;
        Capture = false;
        _zone = OverlayZone.Outside;
        SavePlacement();

        // 拖动过程中不截屏（会卡），停下来重抓一次，让玻璃底对齐新位置
        CaptureBackdrop();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_interactive && !_pressing) Cursor = Cursors.Default;
    }

    private static Cursor CursorFor(OverlayZone zone) => zone switch
    {
        OverlayZone.Left or OverlayZone.Right => Cursors.SizeWE,
        OverlayZone.Top or OverlayZone.Bottom => Cursors.SizeNS,
        OverlayZone.TopLeft or OverlayZone.BottomRight => Cursors.SizeNWSE,
        OverlayZone.TopRight or OverlayZone.BottomLeft => Cursors.SizeNESW,
        OverlayZone.Body => Cursors.SizeAll,
        _ => Cursors.Default,
    };

    // ────────────────────────── 工具 ──────────────────────────

    private static RectI ToRectI(Rectangle rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    private static Rectangle ToRectangle(RectI rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recaptureTimer.Stop();
            _recaptureTimer.Dispose();
            _backdrop?.Dispose();
            _backdrop = null;
            _fonts.Dispose();
        }

        base.Dispose(disposing);
    }
}
