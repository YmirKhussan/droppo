using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Droppo.Core;
using WpfPath = System.Windows.Shapes.Path;

namespace Droppo.Shell;

/// <summary>
/// The radial menu. It's a real drop target: you release the dragged file on a slice,
/// Windows hands us the file paths, and we run that slice's action.
/// </summary>
public partial class WheelWindow : Window
{
    private const double SizeDip = 380, Center = SizeDip / 2, InnerRadius = 60, OuterRadius = 164, GapDegrees = 1.4;
    private const double Lift = 4; // how far the slice under the cursor moves out

    // frosted slices on the glass disc; the one under the cursor turns cobalt
    private static readonly Color SliceColor = Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF);
    private static readonly Color LabelColor = Color.FromRgb(0x0F, 0x2D, 0x5B);
    private static readonly Color HintColor = Color.FromRgb(0x64, 0x74, 0x8B);
    private static readonly Color OnAccent = Colors.White;
    private static readonly Color OnAccentHint = Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF);

    private readonly Func<Settings> _settings;
    private readonly DispatcherTimer _ghostTimer;
    private readonly DispatcherTimer _hideTimer;
    private readonly List<(SolidColorBrush Fill, SolidColorBrush Label, SolidColorBrush Hint, TranslateTransform Shift, double Angle)> _slices = new();

    private IReadOnlyList<WheelAction> _actions = Array.Empty<WheelAction>();
    private IReadOnlyList<string>? _files;
    private int _hover = -1;
    private bool _armed;     // Windows confirmed this is a file drag
    private bool _testMode;  // opened from the tray, waits for a normal drag
    private DateTime _shownAt;
    private NativeMethods.POINT _shownPos;

    /// <summary>User released on a slice.</summary>
    public event Action<WheelAction, IReadOnlyList<string>>? ActionChosen;

    /// <summary>Turned out not to be a file drag; wheel closed itself.</summary>
    public event Action? Aborted;

    public WheelWindow(Func<Settings> settings)
    {
        _settings = settings;
        InitializeComponent();

        _ghostTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _ghostTimer.Tick += OnGhostTick;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _hideTimer.Tick += (_, _) => HideNow();

        SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(this).Handle, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        Hub.MouseLeftButtonUp += (_, _) => HideNow();
        HubMark.Content = Brand.Symbol(24, Brushes.White);
    }

    // ---------- showing and hiding ----------

    /// <summary>Called when the trigger key goes down mid-drag. Starts dim until Windows confirms files.</summary>
    public void ShowAt(NativeMethods.POINT cursor)
    {
        Reset();
        _testMode = false;
        SetHub(mark: true, title: null, sub: "move to load");
        FrostBehind(cursor);
        Place(cursor);
        Motion.To(this, OpacityProperty, 0, 0.35, 90);
        Motion.To(RootScale, ScaleTransform.ScaleXProperty, 0.9, 1, 140);
        Motion.To(RootScale, ScaleTransform.ScaleYProperty, 0.9, 1, 140);
        _shownAt = DateTime.UtcNow;
        _shownPos = cursor;
        _ghostTimer.Start();
    }

    /// <summary>Tray "test" mode: wheel waits in the middle of the screen for a normal drag.</summary>
    public void ShowForTest(NativeMethods.POINT where)
    {
        Reset();
        _testMode = true;
        SetHub(mark: false, title: "Drag a file here", sub: "click to close");
        FrostBehind(where);
        Place(where);
        Motion.To(this, OpacityProperty, 0, 1, 120);
        Motion.To(RootScale, ScaleTransform.ScaleXProperty, 0.9, 1, 160);
        Motion.To(RootScale, ScaleTransform.ScaleYProperty, 0.9, 1, 160);
    }

    /// <summary>For the preview pictures: the wheel as it looks mid-drag, with one slice highlighted.</summary>
    internal void ShowArmedForPreview(NativeMethods.POINT where, IReadOnlyList<string> files, IReadOnlyList<WheelAction> actions, int hover)
    {
        ShowForTest(where);
        _files = files;
        _actions = actions;
        _armed = true;
        BuildSlices();
        SetHub(mark: false, title: FileKinds.Describe(files), sub: "release here to cancel");
        Opacity = 1;
        SetHover(hover);
    }

    public void HideSoon(int milliseconds)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _ghostTimer.Stop();
        _hideTimer.Stop();
        if (IsVisible) Hide();
        Reset();
    }

    private void Reset()
    {
        _hideTimer.Stop();
        _files = null;
        _actions = Array.Empty<WheelAction>();
        _armed = false;
        _hover = -1;
        SliceLayer.Children.Clear();
        _slices.Clear();
    }

    private void SetHub(bool mark, string? title, string? sub)
    {
        HubMark.Visibility = mark ? Visibility.Visible : Visibility.Collapsed;
        HubTitle.Text = title ?? "";
        HubTitle.Visibility = string.IsNullOrEmpty(title) ? Visibility.Collapsed : Visibility.Visible;
        HubSub.Text = sub ?? "";
    }

    private void Abort()
    {
        HideNow();
        Aborted?.Invoke();
    }

    private Brush? _defaultFill;

    /// <summary>
    /// Real glass: grabs what's on screen where the wheel is about to appear, blurs it, and lays milky
    /// white over it (more white on dark screens so the labels stay readable). Must run before the wheel is visible.
    /// </summary>
    private void FrostBehind(NativeMethods.POINT p)
    {
        if (IsVisible) return;
        _defaultFill ??= Backdrop.Fill;
        BehindLayer.Children.Clear();
        double scale = NativeMethods.ScaleAt(p);
        int size = (int)Math.Round(SizeDip * scale);
        var captured = Glass.CaptureBehind(p.X - size / 2, p.Y - size / 2, size, size);
        if (captured is not { } c)
        {
            Backdrop.Fill = _defaultFill; // plain frosted white
            return;
        }
        var image = Glass.BlurredImage(c.Image);
        image.Width = SizeDip;
        image.Height = SizeDip;
        BehindLayer.Children.Add(image);
        double tint = Glass.TintFor(c.Brightness);
        var fill = new LinearGradientBrush(
            Color.FromArgb((byte)(255 * Math.Min(1, tint + 0.08)), 0xFF, 0xFF, 0xFF),
            Color.FromArgb((byte)(255 * tint), 0xF4, 0xF6, 0xFA), new Point(0.2, 0), new Point(0.8, 1));
        fill.Freeze();
        Backdrop.Fill = fill;
    }

    /// <summary>Centres the window on a physical-pixel point, sized for that monitor's scaling.</summary>
    private void Place(NativeMethods.POINT p)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        Position(hwnd, p);
        if (!IsVisible) Show();
        Position(hwnd, p); // again, in case Windows nudged it when it changed monitors
    }

    private void Position(IntPtr hwnd, NativeMethods.POINT p)
    {
        // Fixed design size, not Width/Height: WPF rewrites those after DPI changes and they'd drift.
        double scale = NativeMethods.ScaleAt(p);
        int w = (int)Math.Round(SizeDip * scale), h = w;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, p.X - w / 2, p.Y - h / 2, w, h, NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// While dim: if the mouse keeps moving and Windows never offered us files,
    /// it was a text selection or window drag, not a file. Quietly go away.
    /// </summary>
    private void OnGhostTick(object? sender, EventArgs e)
    {
        if (_armed || _testMode || !IsVisible)
        {
            _ghostTimer.Stop();
            return;
        }
        var p = NativeMethods.CursorPos();
        bool moved = Math.Abs(p.X - _shownPos.X) + Math.Abs(p.Y - _shownPos.Y) > 12;
        if (moved && (DateTime.UtcNow - _shownAt).TotalMilliseconds > 250) Abort();
    }

    // ---------- drag and drop ----------

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_files == null)
        {
            var files = TryGetFiles(e.Data);
            var actions = files == null ? Array.Empty<WheelAction>() : ActionRegistry.For(files, _settings());
            if (files == null || actions.Count == 0)
            {
                e.Effects = DragDropEffects.None;
                if (!_testMode) Dispatcher.BeginInvoke(new Action(Abort)); // don't hide inside Windows' callback
                return;
            }

            _files = files;
            _actions = actions;
            _armed = true;
            _ghostTimer.Stop();
            BuildSlices();
            SetHub(mark: false, title: FileKinds.Describe(files), sub: "release here to cancel");
            Motion.To(this, OpacityProperty, Opacity, 1, 120);
        }
        UpdateHover(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_armed)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        UpdateHover(e);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // Fires when moving between shapes too, so only clear the highlight. DragOver repaints it.
        e.Handled = true;
        SetHover(-1);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        UpdateHover(e);
        int index = _hover;
        var files = _files;
        var action = index >= 0 && index < _actions.Count ? _actions[index] : null;

        HideNow();

        if (action == null || files == null)
        {
            e.Effects = DragDropEffects.None;
            Aborted?.Invoke();
            return;
        }

        // Tell Explorer "copied", never "moved", so the original file is always left alone.
        e.Effects = SafeEffect(e.AllowedEffects);
        Dispatcher.BeginInvoke(new Action(() => ActionChosen?.Invoke(action, files)));
    }

    private void UpdateHover(DragEventArgs e)
    {
        SetHover(SliceAt(e.GetPosition(Root)));
        e.Effects = _hover >= 0 ? SafeEffect(e.AllowedEffects) : DragDropEffects.None;
    }

    private static DragDropEffects SafeEffect(DragDropEffects allowed)
    {
        if ((allowed & DragDropEffects.Copy) != 0) return DragDropEffects.Copy;
        if ((allowed & DragDropEffects.Link) != 0) return DragDropEffects.Link;
        return DragDropEffects.None;
    }

    private static IReadOnlyList<string>? TryGetFiles(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
                return paths;
        }
        catch (Exception ex)
        {
            Log.Write("Could not read dragged data: " + ex.Message);
        }
        return null;
    }

    // ---------- drawing ----------

    private int SliceAt(Point p)
    {
        int n = _actions.Count;
        if (n == 0) return -1;
        double dx = p.X - Center, dy = p.Y - Center;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < InnerRadius - 4 || r > OuterRadius + 8) return -1; // hub = cancel

        double step = 360.0 / n;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;          // 0 = right, 90 = down (screen y goes down)
        double a = ((angle + 90 + step / 2) % 360 + 360) % 360;      // slice 0 is centred at the top
        return Math.Min(n - 1, (int)(a / step));
    }

    private void SetHover(int index)
    {
        if (index == _hover) return;
        int previous = _hover;
        _hover = index;
        if (previous >= 0 && previous < _slices.Count) Highlight(previous, false);
        if (index >= 0 && index < _slices.Count) Highlight(index, true);
        if (_armed) HubSub.Text = index >= 0 ? "release to run" : "release here to cancel";
    }

    private void Highlight(int i, bool on)
    {
        var s = _slices[i];
        Motion.Color(s.Fill, on ? Brand.Cobalt : SliceColor, on ? 70 : 110);
        Motion.Color(s.Label, on ? OnAccent : LabelColor, 70);
        Motion.Color(s.Hint, on ? OnAccentHint : HintColor, 70);
        double rad = s.Angle * Math.PI / 180;
        double dx = on ? Math.Cos(rad) * Lift : 0, dy = on ? Math.Sin(rad) * Lift : 0;
        Motion.To(s.Shift, TranslateTransform.XProperty, s.Shift.X, dx, 90);
        Motion.To(s.Shift, TranslateTransform.YProperty, s.Shift.Y, dy, 90);
    }

    private void BuildSlices()
    {
        SliceLayer.Children.Clear();
        _slices.Clear();
        _hover = -1;

        int n = _actions.Count;
        double step = 360.0 / n;
        double gap = n > 1 ? GapDegrees : 0;

        for (int i = 0; i < n; i++)
        {
            double mid = -90 + i * step;
            double start = mid - step / 2 + gap, end = mid + step / 2 - gap;
            if (end - start > 359.9) end = start + 359.9;

            var fill = new SolidColorBrush(SliceColor);
            var labelBrush = new SolidColorBrush(LabelColor);
            var hintBrush = new SolidColorBrush(HintColor);
            var shift = new TranslateTransform();

            var slice = new WpfPath { Data = Sector(start, end), Fill = fill, RenderTransform = shift };
            SliceLayer.Children.Add(slice);

            var label = new TextBlock
            {
                Text = _actions[i].Label, FontSize = n > 8 ? 15 : 16.5, FontWeight = FontWeights.SemiBold,
                Foreground = labelBrush, TextAlignment = TextAlignment.Center,
            };
            var hint = new TextBlock
            {
                Text = _actions[i].Hint, FontSize = 10.5, Foreground = hintBrush,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
            };
            var stack = new StackPanel { Width = 80, IsHitTestVisible = false, RenderTransform = shift };
            stack.Children.Add(label);
            stack.Children.Add(hint);

            var at = Polar((InnerRadius + OuterRadius) / 2 + 4, mid);
            Canvas.SetLeft(stack, at.X - 40);
            Canvas.SetTop(stack, at.Y - 20);
            SliceLayer.Children.Add(stack);
            _slices.Add((fill, labelBrush, hintBrush, shift, mid));
        }

        // slices fan in quickly so the eye catches where things are
        if (Motion.Enabled) Motion.To(SliceLayer, OpacityProperty, 0, 1, 110);
    }

    private static Geometry Sector(double startDeg, double endDeg)
    {
        bool large = endDeg - startDeg > 180;
        var figure = new PathFigure { StartPoint = Polar(OuterRadius, startDeg), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new ArcSegment(Polar(OuterRadius, endDeg), new Size(OuterRadius, OuterRadius), 0, large, SweepDirection.Clockwise, true));
        figure.Segments.Add(new LineSegment(Polar(InnerRadius, endDeg), true));
        figure.Segments.Add(new ArcSegment(Polar(InnerRadius, startDeg), new Size(InnerRadius, InnerRadius), 0, large, SweepDirection.Counterclockwise, true));
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point Polar(double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(Center + radius * Math.Cos(rad), Center + radius * Math.Sin(rad));
    }
}
