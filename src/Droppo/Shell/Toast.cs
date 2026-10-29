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
    private const double Gap = 20; // around the card, room for its soft shadow

    private readonly Border _card;
    // casts the soft shadow; hidden under the card (the blurred screen picture covers it)
    private readonly Border _shadow = new()
    {
        CornerRadius = new CornerRadius(16), Background = Brushes.White, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Effect = Glass.SoftShadow(0.16),
    };
    // a Canvas so the (taller) screen picture never makes the card taller
    private readonly Canvas _behind = new() { IsHitTestVisible = false, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
    private const double BehindHeight = 230; // screen strip grabbed behind the card (taller than any card)
    private static readonly Brush PlainCard = MakeCardFill(0.97, 0.93);
    private readonly Border _icon = new() { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _title = new() { FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _body = new()
    {
        FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = 52, Margin = new Thickness(0, 2, 0, 0),
    };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Border _progress;
    private readonly Border _bar;
    private bool _measured;
    private readonly TranslateTransform _progressShift = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string? _reveal;

    public Toast()
    {
        Title = "Droppo";
        Width = CardWidth + Gap * 2;
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

        var texts = new StackPanel { Margin = new Thickness(14, 1, 0, 0) };
        texts.Children.Add(_title);
        texts.Children.Add(_body);
        texts.Children.Add(_buttons);

        var track = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.AccentSoft, ClipToBounds = true, Margin = new Thickness(0, 12, 4, 2) };
        _bar = new Border
        {
            Width = 110, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3),
            Background = Ui.Accent, RenderTransform = _progressShift,
        };
        track.Child = _bar;
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
            Child = grid, Width = CardWidth, Padding = new Thickness(16, 16, 14, 16),
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1),
            BorderBrush = Glass.Enabled ? Glass.CardBorder : Ui.Line, Background = PlainCard, Cursor = Cursors.Hand,
        };
        _card.SizeChanged += (_, _) =>
        {
            _behind.Width = _card.ActualWidth;
            _behind.Height = _card.ActualHeight;
            _behind.Clip = new RectangleGeometry(new Rect(0, 0, _card.ActualWidth, _card.ActualHeight), 16, 16);
            _shadow.Width = _card.ActualWidth;
            _shadow.Height = _card.ActualHeight;
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
        var root = new Grid { Margin = new Thickness(Gap) };
        root.Children.Add(_shadow);
        root.Children.Add(_behind);
        root.Children.Add(_card);
        // hairline so the card still has an edge on a white screen
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(17), Margin = new Thickness(-1), BorderThickness = new Thickness(1), IsHitTestVisible = false,
            BorderBrush = Brand.Brush(Color.FromArgb(0x1C, 0x0F, 0x2D, 0x5B)),
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
        // like the board: one filled button for the main thing, a quiet one beside it
        bool canOpen = open != null && File.Exists(open);
        if (canOpen) _buttons.Children.Add(PillButton("Open file", () => { OpenFile(open!); Dismiss(); }, primary: true));
        if (reveal != null) _buttons.Children.Add(PillButton("Show in folder", () => { Tray.Reveal(reveal); Dismiss(); }, primary: !canOpen));
        if (extra is { } x) _buttons.Children.Add(PillButton(x.Label, x.Run, primary: _buttons.Children.Count == 0));
        _buttons.Visibility = _buttons.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        bool working = kind == ToastKind.Working;
        _progress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        _measured = false;
        _bar.BeginAnimation(FrameworkElement.WidthProperty, null);
        _bar.Width = 110;
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
            double right = area.Right - 4 - Gap, bottom = area.Bottom - 4 - Gap;
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

    /// <summary>Swaps the sweeping bar for a real one (0 to 1) and updates the line under the title.</summary>
    public void SetProgress(double fraction, string? text)
    {
        if (!IsVisible || _title.Tag is not ToastKind.Working) return;
        if (!string.IsNullOrWhiteSpace(text) && _body.Text != text)
        {
            _body.Text = text;
            _body.Visibility = Visibility.Visible;
        }
        double track = _progress.ActualWidth;
        if (track <= 0) return;
        if (!_measured)
        {
            _measured = true;
            _progressShift.BeginAnimation(TranslateTransform.XProperty, null);
            _progressShift.X = 0;
        }
        double width = Math.Max(6, track * Math.Clamp(fraction, 0, 1));
        if (Motion.Enabled && Math.Abs(width - _bar.ActualWidth) > 1)
            _bar.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(180)) { EasingFunction = Motion.Out });
        else
        {
            _bar.BeginAnimation(FrameworkElement.WidthProperty, null);
            _bar.Width = width;
        }
    }

    public bool IsWorking => IsVisible && _title.Tag is ToastKind.Working;

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
        Brush bg; FrameworkElement mark; double radius = 20;
        switch (kind)
        {
            case ToastKind.Done:
                bg = Ui.Success;
                mark = new WpfPath
                {
                    Data = Geometry.Parse("M0,5 L4.5,9.5 L13,1"), Stroke = Brushes.White, StrokeThickness = 2.4,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                };
                break;
            case ToastKind.Error:
                bg = Ui.Danger;
                mark = new TextBlock { Text = "!", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
                break;
            case ToastKind.Copied:
                bg = Ui.Success;
                mark = new TextBlock { Text = Ui.Glyph.Copy, FontFamily = Ui.Icons, FontSize = 16, Foreground = Brushes.White };
                break;
            default: // working / info: the Droppo mark on an ice-blue tile
                bg = Ui.AccentSoft;
                radius = 12;
                mark = Brand.Symbol(20, Ui.Accent);
                break;
        }
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Center;
        _icon.Background = bg;
        _icon.CornerRadius = new CornerRadius(radius);
        _icon.Child = mark;
    }

    private static Button PillButton(string label, Action run, bool primary)
    {
        var b = new Button
        {
            Content = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
            Foreground = primary ? Brushes.White : Ui.Accent, Background = primary ? Ui.Accent : Ui.AccentSoft,
            BorderBrush = primary ? Ui.Accent : Ui.AccentSoft, Padding = new Thickness(16, 6, 16, 7), Margin = new Thickness(0, 0, 8, 0),
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
