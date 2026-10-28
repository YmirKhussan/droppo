using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Droppo.Core;
using Droppo.Shell.Studio;

namespace Droppo.Shell;

/// <summary>
/// Every Droppo window: the glass backdrop runs edge to edge, under our own frosted title bar
/// (Windows' grey title bar would cut it off). Set <c>Content</c> as usual; it is framed automatically.
/// With <c>card: true</c> the content sits on one frosted card (dialogs).
/// </summary>
internal class GlassWindow : Window
{
    private const double CaptionHeight = 40;

    private readonly bool _card;
    private readonly TextBlock _caption = new()
    {
        FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 0, 0),
    };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly WindowChrome _chrome = new()
    {
        CaptionHeight = CaptionHeight, ResizeBorderThickness = new Thickness(6), CornerRadius = new CornerRadius(0),
        GlassFrameThickness = new Thickness(0, 0, 0, 1), UseAeroCaptionButtons = false,
    };
    private Border? _frame;
    private UIElement? _body;
    private bool _wrapping;
    private TextBlock? _maxGlyph;

    private readonly bool _overlay;
    private readonly bool _glass;
    private readonly Brush _fill;
    private const double Radius = 12;

    // ---- the glass: a blurred copy of the screen behind, wider than the window so it can slide as you drag ----
    private readonly Canvas _behindLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly System.Windows.Threading.DispatcherTimer _recapture = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private System.Windows.Controls.Image? _behind;
    private Int32Rect _captured;          // screen area of the copy, physical pixels
    private DateTime _capturedAt;
    private bool _canExclude = true;      // Windows can hide this window from its own screen copy (Windows 10 2004+)
    private Grid? _shell;

    /// <param name="card">Put the content on one frosted card (dialogs).</param>
    /// <param name="overlayCaption">No title row: the content fills the window and the window buttons float top-right
    /// (the main window, whose sidebar carries the logo).</param>
    public GlassWindow(bool card = false, bool overlayCaption = false)
    {
        _card = card;
        _overlay = overlayCaption;
        // Glass: a truly see-through window (per-pixel transparency), with Windows blurring what's behind it
        // and a light frost painted on top. Without glass: an ordinary solid window.
        _glass = Glass.Enabled;
        _fill = _glass ? Glass.WindowTint : Glass.WindowBackground;
        if (_glass)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // the rounded frame paints the frost
            _chrome.GlassFrameThickness = new Thickness(0);
            // what's behind the window, blurred: taken before the window first appears (so it can't catch itself)
            SourceInitialized += (_, _) => { Capture(excludeSelf: false); UpdateRegion(); };
            SizeChanged += (_, _) => { UpdateRegion(); PlaceBehind(); if (!Covered()) RecaptureSoon(); };
            LocationChanged += (_, _) => { PlaceBehind(); if (!Covered()) RecaptureSoon(); else RecaptureSoon(slow: true); };
            Activated += (_, _) => { if ((DateTime.UtcNow - _capturedAt).TotalSeconds > 2) RecaptureSoon(); };
            _recapture.Tick += (_, _) => { _recapture.Stop(); Capture(excludeSelf: true); };
        }
        else
        {
            Background = _fill;
            Glass.RoundCorners(this);
        }
        Foreground = Ui.Text;
        FontFamily = Ui.Body;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, _chrome);
        _caption.Foreground = Ui.Text;
        _caption.SetBinding(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        Loaded += (_, _) =>
        {
            BuildButtons();
            if (_card && _body != null) Motion.Rise(_body, 6, 150);
        };
        StateChanged += (_, _) => UpdateState();
    }

    /// <summary>The element passed in as Content (inside the frame).</summary>
    public UIElement? Body => _body;

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (_wrapping || ReferenceEquals(newContent, _frame) || ReferenceEquals(newContent, _shell) || newContent is not UIElement body) return;
        _wrapping = true;
        try
        {
            Content = null; // let go of it first: an element can only have one parent
            var frame = Frame(body);
            if (_glass)
            {
                // layers: the blurred screen copy (clipped to the rounded pane), then the frosted pane with the content
                _shell = new Grid();
                _shell.Children.Add(_behindLayer);
                _shell.Children.Add(frame);
                _shell.SizeChanged += (_, _) => _behindLayer.Clip = new RectangleGeometry(new Rect(0, 0, _shell.ActualWidth, _shell.ActualHeight), _frame!.CornerRadius.TopLeft, _frame.CornerRadius.TopLeft);
                Content = _shell;
            }
            else Content = frame;
        }
        finally { _wrapping = false; }
    }

    private Border Frame(UIElement body)
    {
        var root = new Grid();
        if (_overlay)
        {
            root.Children.Add(body);
            _buttons.VerticalAlignment = VerticalAlignment.Top;
            Panel.SetZIndex(_buttons, 10);
            root.Children.Add(_buttons);
            _body = body;
            return _frame = MakeFrame(root);
        }
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new DockPanel { Height = CaptionHeight, LastChildFill = true };
        var mark = Brand.Tile(18);
        mark.Margin = new Thickness(14, 0, 0, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        DockPanel.SetDock(_buttons, Dock.Right);
        title.Children.Add(mark);
        title.Children.Add(_buttons);
        title.Children.Add(_caption);
        root.Children.Add(title);

        UIElement inner = body;
        if (_card)
        {
            var card = Glass.Card(body, new Thickness(0), 16);
            card.Margin = new Thickness(18, 0, 18, 22); // room for the soft shadow
            inner = card;
        }
        Grid.SetRow(inner, 1);
        root.Children.Add(inner);
        _body = _card ? inner : body;

        return _frame = MakeFrame(root);
    }

    /// <summary>The window's pane: rounded frost with a bright glass edge (or plain when glass is off).</summary>
    private Border MakeFrame(UIElement root) => new()
    {
        Child = root, BorderThickness = new Thickness(1),
        Background = _glass ? _fill : null,
        CornerRadius = new CornerRadius(_glass ? Radius : 0),
        BorderBrush = _glass ? Brand.Brush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)) : Ui.Line,
    };

    // ---------- glass: copy, place, refresh ----------

    private void RecaptureSoon(bool slow = false)
    {
        if (!_glass) return;
        _recapture.Stop();
        _recapture.Interval = TimeSpan.FromMilliseconds(slow ? 700 : 250);
        _recapture.Start();
    }

    private (double X, double Y) Scale()
    {
        var source = PresentationSource.FromVisual(this);
        return (source?.CompositionTarget?.TransformToDevice.M11 ?? 1, source?.CompositionTarget?.TransformToDevice.M22 ?? 1);
    }

    private NativeMethods.RECT WindowRect()
    {
        NativeMethods.GetWindowRect(new System.Windows.Interop.WindowInteropHelper(this).Handle, out var r);
        return r;
    }

    /// <summary>True while the screen copy still covers the whole window.</summary>
    private bool Covered()
    {
        if (_behind == null) return false;
        var r = WindowRect();
        return r.Left >= _captured.X && r.Top >= _captured.Y && r.Right <= _captured.X + _captured.Width && r.Bottom <= _captured.Y + _captured.Height;
    }

    /// <summary>Copies the screen around the window (a quarter of its size extra on every side), blurs it, frosts it.</summary>
    private void Capture(bool excludeSelf)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var r = WindowRect();
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;
        int padX = w / 4, padY = h / 4;
        int x = Math.Max((int)SystemParameters.VirtualScreenLeft, r.Left - padX), y = Math.Max((int)SystemParameters.VirtualScreenTop, r.Top - padY);
        int cw = w + padX * 2, ch = h + padY * 2;

        bool excluded = false;
        if (excludeSelf)
        {
            if (!_canExclude) return; // keep the old copy rather than catch ourselves in it
            excluded = NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
            if (!excluded) { _canExclude = false; Log.Write("Glass can't refresh on this Windows version; it keeps the first copy."); return; }
            NativeMethods.DwmFlush();
        }
        (System.Windows.Media.Imaging.BitmapSource Image, double Brightness)? copy;
        try { copy = Glass.CaptureBehind(x, y, cw, ch); }
        finally { if (excluded) NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_NONE); }

        if (copy is not { } c)
        {
            if (_frame != null && _behind == null) _frame.Background = Glass.WindowBackground; // never a see-through window over nothing
            return;
        }
        _captured = new Int32Rect(x, y, cw, ch);
        _capturedAt = DateTime.UtcNow;
        if (_behind == null)
        {
            _behind = Glass.BlurredImage(c.Image);
            _behindLayer.Children.Add(_behind);
        }
        else _behind.Source = c.Image;
        if (_frame != null) _frame.Background = Glass.FrostFor(c.Brightness);
        PlaceBehind();
    }

    /// <summary>Keeps the copy lined up with the screen as the window moves, so the glass looks fixed in place.</summary>
    private void PlaceBehind()
    {
        if (_behind == null) return;
        var (sx, sy) = Scale();
        var r = WindowRect();
        double margin = _shell?.Margin.Left ?? 0;
        Canvas.SetLeft(_behind, (_captured.X - r.Left) / sx - margin);
        Canvas.SetTop(_behind, (_captured.Y - r.Top) / sy - margin);
        _behind.Width = _captured.Width / sx;
        _behind.Height = _captured.Height / sy;
    }

    /// <summary>Clips the window to the rounded pane.</summary>
    private void UpdateRegion()
    {
        if (!_glass) return;
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (WindowState == WindowState.Maximized) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
            var source = PresentationSource.FromVisual(this);
            double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            int w = (int)Math.Ceiling(ActualWidth * sx), h = (int)Math.Ceiling(ActualHeight * sy), r = (int)Math.Round(Radius * 2 * sx);
            if (w <= 0 || h <= 0) return;
            SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, r, r), true); // Windows owns the region after this
        }
        catch (Exception ex)
        {
            Log.Write("Rounded window edge skipped: " + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    private void BuildButtons()
    {
        _buttons.Children.Clear();
        bool resizable = ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
        _chrome.ResizeBorderThickness = resizable ? new Thickness(6) : new Thickness(0);
        if (resizable || ResizeMode == ResizeMode.CanMinimize)
            _buttons.Children.Add(CaptionButton("", "Minimise", () => SystemCommands.MinimizeWindow(this), danger: false, out _));
        if (resizable)
            _buttons.Children.Add(CaptionButton("", "Maximise", ToggleMaximize, danger: false, out _maxGlyph));
        _buttons.Children.Add(CaptionButton("", "Close", Close, danger: true, out _));
        UpdateState();
    }

    private void ToggleMaximize()
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void UpdateState()
    {
        // a maximised window spills over the screen edge by its resize border; pull the content back in
        if (_shell != null) _shell.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (_frame != null)
        {
            if (_shell == null) _frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            _frame.CornerRadius = new CornerRadius(_glass && WindowState != WindowState.Maximized ? Radius : 0);
        }
        UpdateRegion();
        if (_maxGlyph != null) _maxGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    private static Border CaptionButton(string glyph, string tip, Action click, bool danger, out TextBlock glyphBlock)
    {
        var text = new TextBlock
        {
            Text = glyph, FontFamily = Ui.Icons, FontSize = 10, Foreground = Ui.Text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        glyphBlock = text;
        var button = new Border { Width = 46, Height = CaptionHeight, Background = Brushes.Transparent, Child = text, ToolTip = tip };
        WindowChrome.SetIsHitTestVisibleInChrome(button, true);
        var hover = danger ? Brand.Brush(Color.FromRgb(0xC4, 0x2B, 0x1C)) : Brand.Brush(Color.FromArgb(0x14, 0x0F, 0x2D, 0x5B));
        button.MouseEnter += (_, _) => { button.Background = hover; if (danger) text.Foreground = Brushes.White; };
        button.MouseLeave += (_, _) => { button.Background = Brushes.Transparent; text.Foreground = Ui.Text; };
        button.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
        return button;
    }
}
