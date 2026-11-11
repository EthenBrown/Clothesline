// Clothesline — 顶部触发区域设置。
// 视觉沿用 OnboardingWindow 那套米色卡片 / 棕色强调色，保持整体风格一致。

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Clothesline;

/// <summary>调节晾绳左右两侧的安全边距：留白内不唤出、不拦截鼠标。</summary>
internal sealed class TriggerSettingsWindow : Window
{
    private static TriggerSettingsWindow? _open;

    private static readonly Color Bg = Color.FromRgb(0xFB, 0xF7, 0xF0);
    private static readonly Color Ink = Color.FromRgb(0x2E, 0x2A, 0x24);
    private static readonly Color Muted = Color.FromRgb(0x7A, 0x6E, 0x5E);
    private static readonly Color Accent = Color.FromRgb(0xB0, 0x66, 0x30);
    private static readonly Color Line = Color.FromRgb(0xE8, 0xDC, 0xC8);

    private const string ClickTag = "click";

    private readonly LineWindow _win;
    private readonly Settings _settings;
    private readonly Slider _leftSlider, _rightSlider;
    private readonly TextBlock _leftValue, _rightValue;

    private Point _dragFrom;
    private double _dragLeft, _dragTop;
    private bool _dragging;

    public static void ShowFor(LineWindow win, Settings settings)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new TriggerSettingsWindow(win, settings);
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private TriggerSettingsWindow(LineWindow win, Settings settings)
    {
        _win = win;
        _settings = settings;

        Title = "Clothesline — 触发区域";
        Width = 470;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        var card = new Border
        {
            Background = new SolidColorBrush(Bg),
            BorderBrush = new SolidColorBrush(Line),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Margin = new Thickness(18),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.22 },
        };
        card.MouseLeftButtonDown += OnCardMouseDown;
        card.MouseMove += OnCardMouseMove;
        card.MouseLeftButtonUp += (_, _) => _dragging = false;

        var body = new StackPanel { Margin = new Thickness(30, 24, 30, 24) };

        var close = new TextBlock
        {
            Text = "\u2715",
            FontSize = 14,
            Foreground = new SolidColorBrush(Muted),
            HorizontalAlignment = HorizontalAlignment.Right,
            TextAlignment = TextAlignment.Center,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, -12, -12, 0),
            Padding = new Thickness(10, 7, 10, 7),
            Tag = ClickTag,
        };
        close.MouseEnter += (_, _) => close.Foreground = new SolidColorBrush(Ink);
        close.MouseLeave += (_, _) => close.Foreground = new SolidColorBrush(Muted);
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Close(); };
        body.Children.Add(close);

        body.Children.Add(new TextBlock
        {
            Text = "触发区域",
            FontSize = 23,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Ink),
            Margin = new Thickness(0, -12, 0, 4),
        });
        body.Children.Add(new TextBlock
        {
            Text = "绳子只在两侧留白之间响应鼠标。留白内不唤出、也不拦截点击，"
                 + "窗口标题栏的最小化 / 最大化 / 关闭就不会被绳子挡住。",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 0, 0, 6),
        });
        body.Children.Add(new TextBlock
        {
            Text = "提示：Windows 的窗口按钮在右上角，右侧建议留 120–180。设为 0 即恢复整条顶边都可用。",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x8E, 0x7C)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        });

        _leftSlider = MakeSlider(_settings.SafeInsetLeft, out _leftValue);
        body.Children.Add(Row("左侧留白", _leftSlider, _leftValue));

        _rightSlider = MakeSlider(_settings.SafeInsetRight, out _rightValue);
        body.Children.Add(Row("右侧留白", _rightSlider, _rightValue));

        body.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Line),
            Margin = new Thickness(0, 18, 0, 14),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(Pill("恢复默认", Color.FromRgb(0xEE, 0xE5, 0xD6), Ink, () =>
        {
            _leftSlider.Value = 0;
            _rightSlider.Value = 0;
        }));
        buttons.Children.Add(Pill("取消", Color.FromRgb(0xEE, 0xE5, 0xD6), Ink, Close));
        buttons.Children.Add(Pill("保存", Color.FromRgb(0x3A, 0x33, 0x2A), Colors.White, Save));
        body.Children.Add(buttons);

        card.Child = body;
        Content = card;

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }

    private static Slider MakeSlider(int value, out TextBlock label)
    {
        var s = new Slider
        {
            Minimum = 0,
            Maximum = 300,
            Value = Math.Clamp(value, 0, 300),
            TickFrequency = 10,
            IsSnapToTickEnabled = true,
            Width = 230,
            VerticalAlignment = VerticalAlignment.Center,
            Tag = ClickTag,
        };
        label = new TextBlock
        {
            Text = $"{(int)s.Value}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Accent),
            Width = 46,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        var valueLabel = label;
        s.ValueChanged += (_, _) => valueLabel.Text = $"{(int)s.Value}";
        return s;
    }

    private static UIElement Row(string title, Slider slider, TextBlock label)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var t = new TextBlock
        {
            Text = title,
            FontSize = 13,
            Foreground = new SolidColorBrush(Ink),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(t);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(label, 2);
        grid.Children.Add(label);
        return grid;
    }

    private static Border Pill(string text, Color bg, Color fg, Action onClick)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.Hand,
            Tag = ClickTag,
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(fg),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
            },
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private void Save()
    {
        _settings.SafeInsetLeft = (int)_leftSlider.Value;
        _settings.SafeInsetRight = (int)_rightSlider.Value;
        _settings.Save();
        _win.ApplySafeInsets();
        Diag.Log($"触发区域：左 {_settings.SafeInsetLeft} / 右 {_settings.SafeInsetRight}");
        Close();
    }

    // ---- 自实现的窗口拖动：不用 DragMove，否则按钮的抬起消息会被吃掉 ----

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        _dragging = false;
        if (IsClickable(e.OriginalSource as DependencyObject, sender as DependencyObject)) return;
        _dragFrom = PointToScreen(e.GetPosition(this));
        _dragLeft = Left;
        _dragTop = Top;
        _dragging = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragging = false; return; }
        Point now = PointToScreen(e.GetPosition(this));
        double dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
        Left = _dragLeft + dx;
        Top = _dragTop + dy;
    }

    private static bool IsClickable(DependencyObject? src, DependencyObject? stopAt)
    {
        while (src != null)
        {
            if (ReferenceEquals(src, stopAt)) return false;
            if (src is System.Windows.Controls.Primitives.ButtonBase) return true;
            if (src is Slider) return true;
            if (src is FrameworkElement fe && fe.Tag is string s && s == ClickTag) return true;
            src = src is Visual ? VisualTreeHelper.GetParent(src) : LogicalTreeHelper.GetParent(src);
        }
        return false;
    }
}
