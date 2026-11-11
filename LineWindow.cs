// 顶部晾绳面板。行为规范移植自 Tendedero (macOS) by Alejandro Buján —
// https://github.com/alejandrobujan/tendedero（MIT，版权归原作者）。详见 LICENSE。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.IO.Path;
using System.Windows.Threading;

namespace Clothesline;

/// <summary>屏幕顶部的晾绳面板：透明、置顶、不抢焦点；鼠标触顶滑落，离开收起。</summary>
internal sealed class LineWindow : Window
{
    internal const double StripH = 160;

    private readonly Settings _settings;
    private readonly TranslateTransform _slide = new();
    private readonly List<HangItem> _items = new();
    private readonly MouseHook _mouse = new();
    private readonly ScreenshotWatcher _watcher;
    private TrayIcon? _tray;

    private Grid _root = null!;
    private StackPanel _row = null!;
    private System.Windows.Shapes.Path _line = null!;
    private Border _dropHint = null!;
    private Border _pinBtn = null!;
    private Ellipse _pinHead = null!;
    private System.Windows.Shapes.Path _pinBody = null!;

    private IntPtr _hwnd;
    private uint _taskbarCreated;
    private bool _pinned, _shown;
    private bool _clickThrough = false;
    private uint _exclClipFmt;
    private long _suppressClipboardUntil;
    private readonly ShotDedup _dedup = new();
    private DateTime _peekStart;
    private Border _clearBtn = null!;
    private StackPanel _actionBar = null!;
    private DispatcherTimer? _hideTimer, _peekTimer, _breezeTimer, _dragGuard, _clipDebounce, _hintTimer;

    public bool IsPinned => _pinned;

    public LineWindow(Settings settings)
    {
        _settings = settings;
        _watcher = new ScreenshotWatcher(OnNewFile);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        Title = "Clothesline";
        Width = SystemParameters.PrimaryScreenWidth;
        Height = StripH;
        Left = 0;
        Top = 0;

        BuildVisual();
        HookDrop();
        _slide.Y = -StripH; // 收起

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!_pinned && _shown) HideLine(); };

        MouseEnter += (_, _) => _hideTimer.Stop();
        MouseLeave += (_, _) => { if (_shown && !_pinned) _hideTimer.Start(); };

        Loaded += (_, _) => { DrawLine(); ApplySafeInsets(); ApplyPinnedFromSettings(); };
        SourceInitialized += OnSourceInitialized;
    }

    // ---------- 界面 ----------

    private void BuildVisual()
    {
        _root = new Grid { Background = Brushes.Transparent, RenderTransform = _slide };

        _line = new System.Windows.Shapes.Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xA1, 0x84, 0x5E)),
            StrokeThickness = 2,
        };
        _root.Children.Add(_line);

        var poleBrush = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5E, 0x3C), Color.FromRgb(0x5E, 0x3E, 0x25), 90);
        _root.Children.Add(new Rectangle
        {
            Width = 6, Height = 34, RadiusX = 3, RadiusY = 3, Fill = poleBrush,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(3, 4, 0, 0),
        });
        _root.Children.Add(new Rectangle
        {
            Width = 6, Height = 34, RadiusX = 3, RadiusY = 3, Fill = poleBrush,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 3, 0),
        });

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 22, 0, 0),
        };
        _root.Children.Add(_row);

        // 拖放时的提示：只在有文件拖到绳上时出现
        _dropHint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x3A, 0x33, 0x2A)),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 92, 0, 0),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "松手，挂到绳上",
                FontSize = 12,
                Foreground = Brushes.White,
            },
        };
        _root.Children.Add(_dropHint);
        BuildActionBar();

        Content = _root;
    }

    /// <summary>绳子右端下方的两枚圆形按钮：一键清空 + 图钉。沿用原来的半透明白圆与手绘线条。</summary>
    private void BuildActionBar()
    {
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(8.4, 12.6), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(13.6, 12.6), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(11, 21.6), isStroked: true, isSmoothJoin: false);
        }
        body.Freeze();

        _pinHead = new Ellipse
        {
            Width = 12, Height = 12,
            StrokeThickness = 1.2,
            Stroke = new SolidColorBrush(Colors.White),
        };
        Canvas.SetLeft(_pinHead, 5);
        Canvas.SetTop(_pinHead, 1.5);

        _pinBody = new System.Windows.Shapes.Path
        {
            Data = body,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeLineJoin = PenLineJoin.Round,
        };

        var pinCanvas = new Canvas { Width = 22, Height = 23 };
        pinCanvas.Children.Add(_pinBody);
        pinCanvas.Children.Add(_pinHead);

        _pinBtn = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Cursor = Cursors.Hand,
            Child = pinCanvas,
            ToolTip = "固定在桌面（Ctrl+Alt+T）",
        };
        _pinBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetPinned(!_pinned); };

        // 一键清空：同款圆形按钮，手绘垃圾桶线条
        _clearBtn = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Cursor = Cursors.Hand,
            Child = BuildTrashIcon(),
            Background = new SolidColorBrush(Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF)),
            ToolTip = "清空晾绳（文件保留）\n按住 Shift 点击：连同图片文件丢进回收站",
        };
        _clearBtn.MouseEnter += (_, _) => _clearBtn.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xB0, 0x3A, 0x2A));
        _clearBtn.MouseLeave += (_, _) => _clearBtn.Background = new SolidColorBrush(Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF));
        _clearBtn.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ClearAll(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        };

        _actionBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            // 不放在最右上角：那儿是窗口「关闭」按钮。右侧留白变化时整组按钮一起内移。
            Margin = new Thickness(0, 40, 16, 0),
        };
        _actionBar.Children.Add(_clearBtn);
        _actionBar.Children.Add(new Border { Width = 8 });
        _actionBar.Children.Add(_pinBtn);
        _root.Children.Add(_actionBar);
        UpdatePinVisual();
    }

    /// <summary>垃圾桶图标：白色描边、圆角线帽，跟图钉那套手绘线条一致。</summary>
    private static Canvas BuildTrashIcon()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(6.5, 8), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(19.5, 8), isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(new Point(10.5, 8), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(10.5, 5.5), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(15.5, 5.5), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(15.5, 8), isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(new Point(8.3, 8.5), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(9.2, 20.5), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(16.8, 20.5), isStroked: true, isSmoothJoin: false);
            ctx.LineTo(new Point(17.7, 8.5), isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(new Point(11.5, 11.5), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(11.7, 17.5), isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(new Point(14.5, 11.5), isFilled: false, isClosed: false);
            ctx.LineTo(new Point(14.3, 17.5), isStroked: true, isSmoothJoin: false);
        }
        g.Freeze();
        var p = new System.Windows.Shapes.Path
        {
            Data = g,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.25,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var c = new Canvas { Width = 26, Height = 26 };
        c.Children.Add(p);
        return c;
    }

    /// <summary>图钉的外观跟着固定状态走：灰色=会自己收起，橙色=已固定在桌面。</summary>
    private void UpdatePinVisual()
    {
        var ink = new SolidColorBrush(_pinned ? Color.FromRgb(0xE8, 0x96, 0x54) : Color.FromRgb(0x8A, 0x8A, 0x8A));
        _pinHead.Fill = ink;
        _pinBody.Fill = ink;
        _pinBtn.Background = new SolidColorBrush(_pinned
            ? Color.FromArgb(0xEC, 0xFF, 0xF3, 0xE4)
            : Color.FromArgb(0x96, 0xFF, 0xFF, 0xFF));
    }

    /// <summary>把「拖文件到绳上」接住：支持资源管理器的文件，也支持从网页拖来的位图。</summary>
    private void HookDrop()
    {
        AllowDrop = true;
        _root.AllowDrop = true;
        _root.DragEnter += (_, e) => OnDragOver(e);
        _root.DragOver += (_, e) => OnDragOver(e);
        _root.DragLeave += (_, _) => _dropHint.Visibility = Visibility.Collapsed;
        _root.Drop += OnDrop;
    }

    private void OnDragOver(DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!ok) return;

        _dragGuard?.Stop();        // 真的在拖文件，取消自动收回
        if (!_shown) ShowLine();   // 拖到顶部时绳子可能还没落下，这里补一次
        _hideTimer.Stop();
        SetClickThrough(false);    // 拖放期间别让空白区穿透把 Drop 吃掉
        if (_dropHint.Child is TextBlock tb) tb.Text = "松手，挂到绳上";
        _dropHint.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _dropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
        try
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                HangPaths(files);
            }
            else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
            {
                HangPaths(new[] { SaveDroppedBitmap(bmp) });
            }
        }
        catch (Exception ex) { Diag.Log("拖放挂载失败：" + ex.Message); }
    }

    /// <summary>网页里拖来的位图没有源文件，先落盘再当普通图片挂。</summary>
    private static string SaveDroppedBitmap(BitmapSource bmp)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clothesline", "Hang");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"drop_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
        Diag.Log("拖入的位图已存为 " + path);
        return path;
    }

    private void DrawLine()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(0, 30), isFilled: false, isClosed: false);
            ctx.QuadraticBezierTo(new Point(w / 2, 37), new Point(w, 30), isStroked: true, isSmoothJoin: false);
        }
        g.Freeze();
        _line.Data = g;
    }

    // ---------- Win32 接线 ----------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        int ex = unchecked((int)Native.GetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE).ToInt64());
        ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE, ex);

        SetClickThrough(true);

        Native.RegisterHotKey(_hwnd, 1, Native.MOD_CONTROL | Native.MOD_ALT, 0x54 /* T */);
        Native.AddClipboardFormatListener(_hwnd);
        _exclClipFmt = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
        _taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");

        _mouse.MouseMoved += OnGlobalMouse;
        _mouse.Install();

        _breezeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _breezeTimer.Tick += (_, _) =>
        {
            _breezeTimer.Interval = TimeSpan.FromSeconds(4 + Random.Shared.NextDouble() * 10);
            if (_items.Count > 0) _items[Random.Shared.Next(_items.Count)].Breeze();
        };
        _breezeTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 资源管理器重启广播（消息号是运行时注册的，不能写死常量）
        if (_taskbarCreated != 0 && msg == (int)_taskbarCreated)
        {
            _tray?.ReAdd();
            handled = true;
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Native.WM_MOUSEACTIVATE:
                handled = true;
                return (IntPtr)Native.MA_NOACTIVATE;
            case Native.WM_HOTKEY:
                if (wParam == (IntPtr)1) { TogglePin(); handled = true; }
                break;
            case Native.WM_CLIPBOARDUPDATE:
                ArmClipboardRead();
                break;
            case Native.WM_APP + 1:
                _tray?.HandleTrayMessage(unchecked((int)lParam.ToInt64()));
                handled = true;
                return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero || on == _clickThrough) return;
        _clickThrough = on;
        int ex = unchecked((int)Native.GetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE).ToInt64());
        if (on) ex |= Native.WS_EX_TRANSPARENT;
        else ex &= ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLongPtrW(_hwnd, Native.GWL_EXSTYLE, ex);
    }

    private double DpiScale => _hwnd != IntPtr.Zero ? Native.GetDpiForWindow(_hwnd) / 96.0 : 1.0;

    /// <summary>设置里的左右安全边距生效：按钮组跟着右侧留白内移，免得落进不可点区域。</summary>
    public void ApplySafeInsets()
    {
        if (_actionBar == null) return;
        _actionBar.Margin = new Thickness(0, 40, 16 + _settings.SafeInsetRight, 0);
        RefreshClickThrough();
    }

    /// <summary>
    /// 只有鼠标真的落在卡片或按钮上时才让绳子接鼠标；其余地方一律穿透给下面的窗口。
    /// 这是「绳子挡住最小化 / 最大化 / 关闭」的根治办法——以前整条 160px 带子都吃点击。
    /// </summary>
    private bool IsInteractiveArea(int x, int y)
    {
        double scale = DpiScale;
        double dx = x / scale, dy = y / scale;   // 钩子给的是物理像素，这里换成 WPF 的 DIP
        if (dy < 0 || dy > StripH) return false;
        if (dx < _settings.SafeInsetLeft || dx > Width - _settings.SafeInsetRight) return false;

        // 拖文件时整条绳子都要能接住 Drop，别在这时候收穿透
        if ((Native.GetKeyState(Native.VK_LBUTTON) & 0x8000) != 0) return true;

        double rowW = _row.ActualWidth;
        double rowLeft = (Width - rowW) / 2;
        if (dx >= rowLeft - 8 && dx <= rowLeft + rowW + 8 && dy >= 16 && dy <= 150) return true;

        double barRight = Width - (16 + _settings.SafeInsetRight);
        if (dx >= barRight - 66 && dx <= barRight + 4 && dy >= 34 && dy <= 74) return true;
        return false;
    }

    private bool RefreshClickThrough()
    {
        if (!_shown) { SetClickThrough(true); return false; }
        if (!Native.GetCursorPos(out var p)) { SetClickThrough(true); return false; }
        bool interactive = IsInteractiveArea(p.X, p.Y);
        SetClickThrough(!interactive);
        return interactive;
    }

    // ---------- 显示 / 收起 ----------

    private void OnGlobalMouse(int x, int y)
    {
        // 无论是否固定：显示中时按「是否落在卡片/按钮上」实时调整穿透，空白一律让给下面的窗口。
        // 顺带自己管自动收起：空白区是穿透的，WPF 收不到 MouseLeave，不能只靠那个事件。
        if (_shown)
        {
            RefreshClickThrough();
            if (!_pinned)
            {
                // 收起沿用原来的语义：光标还在条带里就别收；自动露头期间交给 peek 计时器。
                bool insideStrip = IsPointInStrip(x, y);
                bool peeking = _peekTimer != null && _peekTimer.IsEnabled;
                if (insideStrip || peeking) _hideTimer?.Stop();
                else if (_hideTimer != null && !_hideTimer.IsEnabled) _hideTimer.Start();
            }
        }
        else if (!_pinned && (Native.GetKeyState(Native.VK_LBUTTON) & 0x8000) == 0)
        {
            SetClickThrough(true);
        }

        if (_pinned || _shown) return;

        // 平时要真正触顶(y<=2)才唤出；但"按住左键往上传文件"这个动作很难正好压在 2px 里，
        // 所以拖拽状态下把判定放宽到 40px —— 拖到屏幕顶部附近绳子就滑下来接住。
        bool dragging = (Native.GetKeyState(Native.VK_LBUTTON) & 0x8000) != 0;
        int trigger = dragging ? 40 : 2;
        if (y > trigger) return;

        int w = Native.GetSystemMetrics(0);
        if (x < 0 || x > w) return; // 只管主屏

        // 左右安全边距内不唤出：两端通常是窗口控制按钮 / 应用菜单，别去抢那几个像素。
        double scale = DpiScale;
        if (x < _settings.SafeInsetLeft * scale ||
            x > (Width - _settings.SafeInsetRight) * scale) return;

        if (IsForegroundFullscreen()) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_shown || _pinned) return;
            ShowLine();
            if (dragging) ArmDragIdleGuard(); // 万一只是在上边缘拖选文字，1.5 秒内没文件进来就收回
        });
    }

    /// <summary>因拖拽而露出的绳子，若迟迟没有文件拖进来（不是真的在拖文件），自动收回。</summary>
    private void ArmDragIdleGuard()
    {
        _dragGuard ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _dragGuard.Stop();
        _dragGuard.Tick -= OnDragGuardTick;
        _dragGuard.Tick += OnDragGuardTick;
        _dragGuard.Start();
    }

    private void OnDragGuardTick(object? sender, EventArgs e)
    {
        _dragGuard?.Stop();
        if (_dropHint.Visibility != Visibility.Visible && !_pinned && _shown) HideLine();
    }

    private static bool IsForegroundFullscreen()
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var sb = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, sb, 64);
        var cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        int w = Native.GetSystemMetrics(0), h = Native.GetSystemMetrics(1);
        return r.Left <= 0 && r.Top <= 0 && r.Right >= w && r.Bottom >= h;
    }

    private void ShowLine()
    {
        _shown = true;
        _hideTimer.Stop();
        RefreshClickThrough();
        SlideTo(0, TimeSpan.FromSeconds(0.55), new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut });
    }

    private void HideLine()
    {
        _shown = false;
        SetClickThrough(true);
        SlideTo(-StripH, TimeSpan.FromSeconds(0.3), new CubicEase { EasingMode = EasingMode.EaseIn });
    }

    private void SlideTo(double to, TimeSpan dur, IEasingFunction ease)
    {
        double cur = _slide.Y; // 读取当前（含动画）值，避免收起/落下切换时跳变
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.Y = cur;
        var a = new DoubleAnimation(to, dur) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        _slide.BeginAnimation(TranslateTransform.YProperty, a);
    }

    public void TogglePin() => SetPinned(!_pinned);

    /// <summary>
    /// 把晾绳固定在桌面（true）/ 取消固定并收起（false）。
    /// 这个状态会写进设置，下次启动照旧——想要"永远挂着"的人不用每次重新按。
    /// </summary>
    public void SetPinned(bool on)
    {
        if (on == _pinned)
        {
            if (on && !_shown) { ShowLine(); RefreshClickThrough(); }
            return;
        }

        _pinned = on;
        _settings.PinnedToDesktop = on;
        _settings.Save();
        _hideTimer.Stop();
        _peekTimer?.Stop();

        if (on)
        {
            ShowLine();
            RefreshClickThrough();
        }
        else
        {
            SetClickThrough(true); // 收起前先穿透，免得滑动途中还挡着下面
            HideLine();
        }

        UpdatePinVisual();
        _tray?.SetCount(_items.Count); // 托盘提示里的"（已固定）"跟着变
        Diag.Log(on ? "晾绳：已固定在桌面（下次启动仍是）" : "晾绳：取消固定，恢复自动收起");
    }

    /// <summary>启动时若上次是固定状态，直接挂出来。</summary>
    private void ApplyPinnedFromSettings()
    {
        if (!_settings.PinnedToDesktop) return;
        _pinned = true;
        ShowLine();
        RefreshClickThrough();
        UpdatePinVisual();
        Diag.Log("晾绳：按上次的设置，启动即固定在桌面");
    }

    /// <summary>屏幕坐标（物理像素）是否落在绳区里。</summary>
    private bool IsPointInStrip(int x, int y)
    {
        double scale = DpiScale;
        return x >= 0 && x <= Width * scale && y >= 0 && y <= StripH * scale;
    }

    /// <summary>滑下绳子并保持若干秒后自动收起（给说明窗"试一试"和右键菜单用）。</summary>
    public void Peek(int seconds)
    {
        if (_pinned) return;
        ShowLine();
        _peekStart = DateTime.Now;
        _peekTimer ??= new DispatcherTimer();
        _peekTimer.Stop();
        _peekTimer.Tick -= OnPeekTick;
        _peekTimer.Tick += OnPeekTick;
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, seconds));
        _peekTimer.Start();
    }

    /// <summary>把外部文件（右键菜单 / 命令行 / 已运行实例转发）挂到绳上。</summary>
    public void HangPaths(IEnumerable<string> paths)
    {
        int n = 0;
        foreach (string p in paths)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                string ext = Path.GetExtension(p).ToLowerInvariant();
                bool video = ext is ".mp4" or ".mov" or ".m4v" or ".mkv" or ".avi" or ".wmv" or ".webm";
                if (AddItem(p, video)) n++;
            }
            catch (Exception e) { Diag.Log($"挂载外部文件失败（{p}）：{e.Message}"); }
        }
        Diag.Log($"外部挂载：{n} 个");
        if (n > 0) Peek(4);
    }

    private void PeekBriefly()
    {
        ShowLine();
        _peekStart = DateTime.Now;
        _peekTimer ??= new DispatcherTimer();
        _peekTimer.Stop();
        _peekTimer.Tick -= OnPeekTick;
        _peekTimer.Tick += OnPeekTick;
        _peekTimer.Interval = TimeSpan.FromSeconds(1.8);
        _peekTimer.Start();
    }

    private void OnPeekTick(object? sender, EventArgs e)
    {
        _peekTimer!.Stop();
        if (_pinned) return;
        // 光标停在绳区可以多留一会儿，但最多续到 6 秒；否则绳子会一直挂着挡住窗口标题栏。
        if (IsMouseInsideStrip() && (DateTime.Now - _peekStart).TotalSeconds < 6) { _peekTimer.Start(); return; }
        if (_shown) HideLine();
    }

    private bool IsMouseInsideStrip()
    {
        if (!Native.GetCursorPos(out var p)) return true;
        return IsPointInStrip(p.X, p.Y);
    }

    // ---------- 挂图 ----------

    public void AttachTray(TrayIcon tray)
    {
        _tray = tray;
        tray.SetCount(_items.Count);
    }

    public void StartWatchers() => _watcher.Start();

    private void OnNewFile(string path, bool isVideo)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                var info = isVideo ? null : TryGetImageInfo(path);
                if (info is { } s && _dedup.ShouldSkip(s.W, s.H, s.Fp, false, DateTime.Now))
                {
                    Diag.Log($"文件夹：{Path.GetFileName(path)} 与刚挂上的图是同一次截图，跳过");
                    return;
                }
                if (AddItem(path, isVideo) && info is { } s2)
                    _dedup.Mark(s2.W, s2.H, s2.Fp, false, DateTime.Now);
            }
            catch { }
        });
    }

    /// <summary>把一张图挂到绳上；返回是否真的挂上了（图片读不出来就不挂）。</summary>
    public bool AddItem(string path, bool isVideo, ImageSource? preview = null)
    {
        ImageSource? thumb = preview;
        if (thumb == null && !isVideo) thumb = LoadThumbnail(path);
        if (thumb == null && !isVideo) return false; // 图片读不出来就不挂

        var item = new HangItem(path, isVideo, thumb);
        HookItem(item);
        _row.Children.Add(item.Visual);
        item.Visual.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.3)));
        item.Swing((Random.Shared.NextDouble() - 0.5) * 14);
        _items.Add(item);

        TrimOverflow();
        _tray?.SetCount(_items.Count);

        if (!_shown && !_pinned) PeekBriefly(); // 新截图来了，露个头提示
        return true;
    }

    private static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            var di = new BitmapImage();
            di.BeginInit();
            di.CacheOption = BitmapCacheOption.OnLoad;
            di.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            di.DecodePixelWidth = 320;
            di.UriSource = new Uri(path);
            di.EndInit();
            di.Freeze();
            return di;
        }
        catch { return null; }
    }

    private void HookItem(HangItem item)
    {
        item.CopyClick += it =>
        {
            try
            {
                var bmp = BitmapFrame.Create(new Uri(it.FilePath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                _suppressClipboardUntil = Environment.TickCount64 + 1200; // 别把自己的复制又挂上去
                Clipboard.SetImage(bmp);
                it.FlashCopied();
            }
            catch { }
        };
        item.OpenClick += it =>
        {
            try { _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(it.FilePath) { UseShellExecute = true }); } catch { }
        };
        item.UnhangClick += Unhang;
        item.DeleteClick += it =>
        {
            // 先把卡片取下来：文件删不掉（被占用 / 路径异常）也不该让图一直赖在绳上。
            Unhang(it);
            try
            {
                if (!DeleteToRecycleBin(it.FilePath))
                    Diag.Log("丢进回收站失败（已从绳上取下）：" + it.FilePath);
            }
            catch (Exception ex) { Diag.Log("删除异常（已从绳上取下）：" + ex.Message); }
        };
    }

    /// <summary>一键清空：默认只从绳上取下、文件保留；按 Shift 点时连文件一起进回收站。</summary>
    public void ClearAll(bool deleteFiles)
    {
        if (_items.Count == 0) { ShowHint("晾绳已经是空的"); return; }

        var snapshot = _items.ToArray();
        _items.Clear();
        foreach (var it in snapshot) _row.Children.Remove(it.Visual);

        if (deleteFiles)
        {
            foreach (var it in snapshot)
            {
                try
                {
                    if (!DeleteToRecycleBin(it.FilePath))
                        Diag.Log("清空：删除文件失败 " + it.FilePath);
                }
                catch (Exception ex) { Diag.Log("清空：删除异常 " + ex.Message); }
            }
        }

        _tray?.SetCount(0);
        ShowHint(deleteFiles ? $"已清空 {snapshot.Length} 张 · 文件已进回收站"
                             : $"已清空 {snapshot.Length} 张 · 文件保留");
        Diag.Log($"一键清空：{snapshot.Length} 张，删文件={deleteFiles}");
    }

    /// <summary>借拖放提示那块牌子显示一句话，不新增视觉元素。</summary>
    private void ShowHint(string text)
    {
        if (_dropHint.Child is TextBlock tb) tb.Text = text;
        _dropHint.Visibility = Visibility.Visible;
        _hintTimer?.Stop();
        _hintTimer ??= new DispatcherTimer();
        _hintTimer.Interval = TimeSpan.FromSeconds(1.6);
        _hintTimer.Tick -= OnHintTick;
        _hintTimer.Tick += OnHintTick;
        _hintTimer.Start();
    }

    private void OnHintTick(object? sender, EventArgs e)
    {
        _hintTimer?.Stop();
        _dropHint.Visibility = Visibility.Collapsed;
    }

    private void Unhang(HangItem it)
    {
        _items.Remove(it);
        _row.Children.Remove(it.Visual);
        _tray?.SetCount(_items.Count);
    }

    private void TrimOverflow()
    {
        int max = (int)((Width - 20) / 174);
        if (max < 3) max = 3;
        if (max > 14) max = 14;
        while (_items.Count > max)
        {
            var old = _items[0];
            _items.RemoveAt(0);
            _row.Children.Remove(old.Visual);
        }
    }

    private static bool DeleteToRecycleBin(string path)
    {
        var op = new Native.SHFILEOPSTRUCTW
        {
            wFunc = Native.FO_DELETE,
            pFrom = path + "\0", // 双 null 结尾
            fFlags = Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_SILENT | Native.FOF_NOERRORUI,
        };
        return Native.SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // ---------- 剪贴板 ----------

    private void OnClipboardChanged()
    {
        if (!_settings.AutoHangClipboard) return;
        if (Environment.TickCount64 < _suppressClipboardUntil) return;
        try
        {
            // 复制文件（资源管理器 Ctrl+C）不算截图，交给拖放/右键菜单那条路
            if (Clipboard.ContainsFileDropList() || Clipboard.ContainsAudio()) return;
            if (!Clipboard.ContainsImage()) return;
            if (_exclClipFmt != 0 && Native.IsClipboardFormatAvailable(_exclClipFmt)) return; // 密码管理器标记的私有内容

            var raw = TryGetClipboardImage();   // 剪贴板正被别的程序占用时，重试几次
            if (raw == null || raw.PixelWidth < 8 || raw.PixelHeight < 8) return;
            var img = NormalizeAlpha(raw);
            string fp = Fingerprint(img) ?? "";
            if (_dedup.ShouldSkip(img.PixelWidth, img.PixelHeight, fp, true, DateTime.Now))
            {
                Diag.Log($"剪贴板：{img.PixelWidth}×{img.PixelHeight} 与刚挂上的图是同一次截图，跳过");
                return;
            }

            var store = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Clothesline", "Hang");
            Directory.CreateDirectory(store);
            var file = Path.Combine(store, $"clip_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(file)) enc.Save(fs);
            Diag.Log($"剪贴板：挂上 {img.PixelWidth}×{img.PixelHeight} 图片 → {Path.GetFileName(file)}");
            if (AddItem(file, false, img))
                _dedup.Mark(img.PixelWidth, img.PixelHeight, fp, true, DateTime.Now);
        }
        catch (Exception e) { Diag.Log("剪贴板读取失败：" + e.Message); }
    }

    /// <summary>
    /// 一次截图会连发好几条 WM_CLIPBOARDUPDATE（先放 DIB，随后补 PNG 等格式）。
    /// 以前每条都立刻读一次剪贴板，同一次截图就必挂 2~3 张——这是重复挂载的主因。
    /// 现在等剪贴板安静 350ms 再读，只读一次；之后还有尺寸 + 指纹判重兜底。
    /// </summary>
    private void ArmClipboardRead()
    {
        _clipDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _clipDebounce.Stop();
        _clipDebounce.Tick -= OnClipboardTick;
        _clipDebounce.Tick += OnClipboardTick;
        _clipDebounce.Start();
    }

    private void OnClipboardTick(object? sender, EventArgs e)
    {
        _clipDebounce?.Stop();
        OnClipboardChanged();
    }

    /// <summary>缩到 16×16 灰度并量化，得到一张图的粗略「指纹」，用来区分同尺寸的不同截图。</summary>
    private static string? Fingerprint(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src,
                new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            var gray = new FormatConvertedBitmap(small, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            var px = new byte[256];
            gray.CopyPixels(px, 16, 0);
            var sb = new StringBuilder(256);
            foreach (byte b in px) sb.Append((char)('a' + (b >> 6)));
            return sb.ToString();
        }
        catch { return null; }
    }

    /// <summary>只读图片头拿真实像素尺寸和指纹；不能拿 LoadThumbnail 的 320 宽缩略图去比。</summary>
    private static (int W, int H, string Fp)? TryGetImageInfo(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dec = BitmapDecoder.Create(fs,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.None);
            var f = dec.Frames[0];
            return (f.PixelWidth, f.PixelHeight, Fingerprint(f) ?? "");
        }
        catch { return null; }
    }

    /// <summary>微信/QQ 截图刚写完剪贴板时可能还被占用，重试几次再放弃。</summary>
    private static System.Windows.Media.Imaging.BitmapSource? TryGetClipboardImage()
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var img = Clipboard.GetImage();
                if (img != null) return img;
            }
            catch { }
            System.Threading.Thread.Sleep(80);
        }
        return null;
    }

    /// <summary>
    /// CF_DIB 里 32 位位图的 alpha 字节常常是垃圾（多半全 0）。WPF 照单全收会把整张图当成全透明，
    /// 相框里就只剩一块灰。这里抽样看 alpha：几乎全 0 就视为"本来就没有透明"，转成 Bgr24。
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource NormalizeAlpha(System.Windows.Media.Imaging.BitmapSource src)
    {
        if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32) return src;

        int w = src.PixelWidth, h = src.PixelHeight;
        if (w <= 0 || h <= 0) return src;

        int stride = w * 4;
        var row = new byte[stride];
        long total = 0, opaque = 0;
        int stepY = Math.Max(1, h / 40);
        for (int y = 0; y < h; y += stepY)
        {
            try { src.CopyPixels(new Int32Rect(0, y, w, 1), row, stride, 0); }
            catch { return src; }
            for (int x = 3; x < stride; x += 4)
            {
                total++;
                if (row[x] != 0) opaque++;
            }
        }
        if (total == 0 || opaque * 100 / total >= 5) return src;   // alpha 有效，别动

        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
        conv.Freeze();
        Diag.Log("剪贴板：alpha 通道为空，按无透明处理");
        return conv;
    }

    // ---------- 退出清理 ----------

    public void RequestExit() => Application.Current.Shutdown();

    public void Cleanup()
    {
        try { if (_hwnd != IntPtr.Zero) Native.UnregisterHotKey(_hwnd, 1); } catch { }
        try { if (_hwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(_hwnd); } catch { }
        _mouse.Dispose();
        _watcher.Dispose();
        _breezeTimer?.Stop();
        _hideTimer?.Stop();
        _peekTimer?.Stop();
        _clipDebounce?.Stop();
        _hintTimer?.Stop();
    }
}
