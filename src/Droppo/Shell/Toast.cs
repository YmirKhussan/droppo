using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Droppo.Core;
using Droppo.Shell.Studio;
using WpfPath = System.Windows.Shapes.Path;

namespace Droppo.Shell;

internal enum ToastKind { Working, Done, Error, Info, Copied }

/// <summary>
/// Droppo's own notification, bottom-right above the clock: "Working on it..." while something slow runs,
/// then "All done." with Show in folder / Open. Never takes focus away from what you're doing.
/// </summary>
internal sealed class Toast : Window
{
    private const double CardWidth = 360;

    private readonly Border _card;
    // a Canvas so the (taller) screen picture never makes the card taller
    private readonly Canvas _behind = new() { IsHitTestVisible = false, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private const double BehindHeight = 230; // screen strip grabbed behind the card (taller than any card)
    private static readonly Brush PlainCard = MakeCardFill(0.97, 0.93);
    private readonly Border _icon = new() { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _body = new()
    {
        FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = 52, Margin = new Thickness(0, 2, 0, 0),
    };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(-8, 6, 0, 0) };
    private readonly Border _progress;
    private readonly TranslateTransform _progressShift = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _reveal;

    public Toast()
    {
        Title = "Droppo";
        Width = CardWidth + 24;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FontFamily = Ui.Body;
        UseLayoutRounding = true;

        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 10 }, Padding = new Thickness(6),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Foreground = Ui.Muted,
            VerticalAlignment = VerticalAlignment.Top, ToolTip = "Close", Margin = new Thickness(4, -4, -6, 0),
        };
        close.Click += (_, _) => Dismiss();

        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        texts.Children.Add(_title);
        texts.Children.Add(_body);
        texts.Children.Add(_buttons);

        var track = new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = Ui.AccentSoft, ClipToBounds = true, Margin = new Thickness(0, 8, 0, 2) };
        track.Child = new Border
        {
            Width = 90, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(1.5),
            Background = Ui.Accent, RenderTransform = _progressShift,
        };
        _progress = track;
        texts.Children.Add(_progress);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(close, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(texts);
        grid.Children.Add(close);

        // frosted card: blurred screen behind, milky white over it, a bright edge
        _card = new Border
        {
            Child = grid, Width = CardWidth, Padding = new Thickness(16, 14, 14, 14),
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1),
            BorderBrush = Glass.Enabled ? Glass.CardBorder : Ui.Line, Background = PlainCard, Cursor = Cursors.Hand,
        };
        _card.SizeChanged += (_, _) =>
        {
            _behind.Width = _card.ActualWidth;
            _behind.Height = _card.ActualHeight;
            _behind.Clip = new RectangleGeometry(new Rect(0, 0, _card.ActualWidth, _card.ActualHeight), 14, 14);
            // the picture lines up with the card's bottom edge (that's where it was taken from)
            foreach (UIElement child in _behind.Children) Canvas.SetTop(child, _card.ActualHeight - BehindHeight);
        };
        _card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInButton(d)) return;
            if (_reveal != null) Tray.Reveal(_reveal);
            Dismiss();
        };
        _card.MouseEnter += (_, _) => _timer.Stop();
        _card.MouseLeave += (_, _) => { if (_title.Tag is not ToastKind.Working) _timer.Start(); };
        var root = new Grid { Margin = new Thickness(12) };
        root.Children.Add(_behind);
        root.Children.Add(_card);
        // hairline so the card still has an edge on a white screen
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(15), Margin = new Thickness(-1), BorderThickness = new Thickness(1), IsHitTestVisible = false,
            BorderBrush = Brand.Brush(Color.FromArgb(0x24, 0x1C, 0x2C, 0x49)),
        });
        Content = root;

        _timer.Tick += (_, _) => Dismiss();
        SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
        SizeChanged += (_, _) => Position();
    }

    /// <summary>False while a full-screen app, game or presentation is up: then Windows' own quiet rules apply.</summary>
    public static bool CanShowNow()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) != 0) return true;
            return state is not (QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE);
        }
        catch
        {
            return true;
        }
    }

    public void Show(ToastKind kind, string title, string body, string? reveal = null, string? open = null, (string Label, Action Run)? extra = null)
    {
        _reveal = reveal;
        _title.Text = title;
        _title.Tag = kind;
        _body.Text = body;
        _body.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        SetIcon(kind);

        _buttons.Children.Clear();
        if (reveal != null) _buttons.Children.Add(LinkButton("Show in folder", () => Tray.Reveal(reveal)));
        if (open != null && File.Exists(open)) _buttons.Children.Add(LinkButton("Open", () => OpenFile(open)));
        if (extra is { } x) _buttons.Children.Add(LinkButton(x.Label, x.Run));
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool working = kind == ToastKind.Working;
        _progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        if (working && Motion.Enabled)
        {
            var sweep = new DoubleAnimation(-90, CardWidth - 60, TimeSpan.FromMilliseconds(1100)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Out };
            _progressShift.BeginAnimation(TranslateTransform.XProperty, sweep);
        }
        else
        {
            _progressShift.BeginAnimation(TranslateTransform.XProperty, null); // the sweep only runs while it's visible
            _progressShift.X = working ? (CardWidth - 120) / 2 : 0;
        }

        _timer.Stop();
        if (!working)
        {
            _timer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 9 : 5);
            _timer.Start();
        }

        bool wasVisible = IsVisible;
        if (!wasVisible) FrostBehind();
        if (!wasVisible) Show();
        Position();
        if (!wasVisible) Motion.Rise(_card, 14, 180);
        else Motion.FadeIn(_body, 120);
    }

    /// <summary>Real glass: blur what's on screen where the card will appear (before it's visible).</summary>
    private void FrostBehind()
    {
        _behind.Children.Clear();
        _card.Background = PlainCard;
        try
        {
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            var area = SystemParameters.WorkArea;
            if (screen == null || area.Width <= 0) return;
            double scale = screen.WorkingArea.Width / area.Width;
            double right = area.Right - 4 - 12, bottom = area.Bottom - 4 - 12;
            int x = (int)Math.Round((right - CardWidth) * scale), y = (int)Math.Round((bottom - BehindHeight) * scale);
            var captured = Glass.CaptureBehind(x, y, (int)Math.Round(CardWidth * scale), (int)Math.Round(BehindHeight * scale));
            if (captured is not { } c) return;
            var image = Glass.BlurredImage(c.Image);
            image.Width = CardWidth;
            image.Height = BehindHeight;
            Canvas.SetTop(image, _card.ActualHeight > 0 ? _card.ActualHeight - BehindHeight : 0);
            _behind.Children.Add(image);
            double tint = Glass.TintFor(c.Brightness);
            _card.Background = MakeCardFill(Math.Min(1, tint + 0.08), tint);
        }
        catch (Exception ex)
        {
            Log.Write("Toast glass skipped: " + ex.Message);
        }
    }

    private static Brush MakeCardFill(double top, double bottom)
    {
        var b = new LinearGradientBrush(Color.FromArgb((byte)(255 * top), 0xFF, 0xFF, 0xFF), Color.FromArgb((byte)(255 * bottom), 0xF4, 0xF6, 0xFA), 90);
        b.Freeze();
        return b;
    }

    public void Dismiss()
    {
        _timer.Stop();
        _progressShift.BeginAnimation(TranslateTransform.XProperty, null);
        if (IsVisible) Hide();
    }

    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    private void SetIcon(ToastKind kind)
    {
        Brush bg; FrameworkElement mark;
        switch (kind)
        {
            case ToastKind.Done:
                bg = Ui.Accent;
                mark = new WpfPath
                {
                    Data = Geometry.Parse("M0,5 L4.5,9.5 L13,1"), Stroke = Brushes.White, StrokeThickness = 2.2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                };
                break;
            case ToastKind.Error:
                bg = Ui.Danger;
                mark = new TextBlock { Text = "!", FontSize = 18, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
                break;
            case ToastKind.Copied:
                bg = Ui.Accent;
                mark = new TextBlock { Text = Ui.Glyph.Copy, FontFamily = Ui.Icons, FontSize = 15, Foreground = Brushes.White };
                break;
            default:
                bg = Ui.AccentSoft;
                mark = Brand.Symbol(20, Ui.Accent, 2.4);
                break;
        }
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        _icon.Background = bg;
        _icon.Child = mark;
    }

    private static Button LinkButton(string label, Action run)
    {
        var b = new Button
        {
            Content = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Accent,
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 0, 2, 0),
        };
        b.Click += (_, _) => run();
        return b;
    }

    private static bool IsInButton(DependencyObject d)
    {
        for (var node = d; node != null; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (node is Button) return true;
        return false;
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Couldn't open " + path + ": " + ex.Message); }
    }

    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
