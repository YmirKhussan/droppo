using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
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

    /// <param name="card">Put the content on one frosted card (dialogs).</param>
    /// <param name="overlayCaption">No title row: the content fills the window and the window buttons float top-right
    /// (the main window, whose sidebar carries the logo).</param>
    public GlassWindow(bool card = false, bool overlayCaption = false)
    {
        _card = card;
        _overlay = overlayCaption;
        // see-through: Windows blurs what's behind, we lay a light frost over it.
        // If the blur can't be switched on, fall back to the painted sky backdrop (never a grey see-through window).
        bool glass = Glass.Enabled;
        Background = glass ? Glass.WindowTint : Glass.WindowBackground;
        if (glass) _chrome.GlassFrameThickness = new Thickness(-1);
        SourceInitialized += (_, _) =>
        {
            if (!glass) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            if (!Glass.EnableBlurBehind(hwnd))
            {
                Background = Glass.WindowBackground;
                Log.Write("Window blur unavailable, using the painted backdrop.");
            }
        };
        Foreground = Ui.Text;
        FontFamily = Ui.Body;
        UseLayoutRounding = true;
        WindowChrome.SetWindowChrome(this, _chrome);
        Glass.RoundCorners(this);
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
            return _frame = new Border { Child = root, BorderThickness = new Thickness(1), BorderBrush = Glass.Enabled ? Brand.Brush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) : Ui.Line };
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

        _frame = new Border
        {
            Child = root, BorderThickness = new Thickness(1),
            BorderBrush = Glass.Enabled ? Brand.Brush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)) : Ui.Line,
        };
        return _frame;
    }

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
        if (_frame != null) _frame.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
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
