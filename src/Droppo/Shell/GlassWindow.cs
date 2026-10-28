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
            SourceInitialized += (_, _) =>
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (!Glass.EnableBlurBehind(hwnd)) Log.Write("Window blur unavailable: the glass stays see-through but unblurred.");
                UpdateRegion();
            };
            SizeChanged += (_, _) => UpdateRegion();
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
        if (_wrapping || ReferenceEquals(newContent, _frame) || newContent is not UIElement body) return;
        _wrapping = true;
        try
        {
            Content = null; // let go of it first: an element can only have one parent
            Content = Frame(body);
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

    /// <summary>Clips the window (and Windows' blur behind it) to the rounded pane.</summary>
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
        if (_frame != null)
        {
            _frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
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
