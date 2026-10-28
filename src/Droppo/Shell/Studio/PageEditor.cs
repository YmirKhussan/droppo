using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Droppo.Core;
using Droppo.Core.Studio;

namespace Droppo.Shell.Studio;

internal enum EditTool { Select, Text, Replace, WhiteOut, Highlight, Draw, Sign, Image }

/// <summary>
/// Edits one page. The page canvas works in PDF points in the page's own orientation; the
/// view is scaled and rotated on top, so mouse positions come back in page coordinates for free.
/// </summary>
internal sealed class PageEditor : Grid
{
    private const double RenderDpi = 150;

    private readonly StudioWindow _host;
    private readonly Func<ActionContext> _context;
    private readonly string _work;

    private readonly Image _pageImage = new() { Stretch = Stretch.Fill };
    private readonly Canvas _layer = new() { Background = Brushes.Transparent, ClipToBounds = true };
    private readonly Canvas _fieldLayer = new() { ClipToBounds = true }; // the PDF's own fillable boxes, on top
    private readonly ComboBox _fontBox = new() { Width = 170, Height = 30, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Font for new or selected text" };
    private readonly Grid _pageHost = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _pageLabel = Ui.Txt("", 13, bold: true);
    private readonly TextBlock _sizeLabel = Ui.Txt("12", 12);
    private readonly Dictionary<EditTool, Button> _toolButtons = new();
    private readonly Dictionary<Rgb, Border> _swatches = new();
    private readonly Stack<List<PageEdit>> _undo = new();

    private List<StudioPage> _pages = new();
    private int _index;
    private StudioPage? _page;
    private string? _renderPng;
    private double _zoom = 1;
    private bool _fitPending;

    private EditTool _tool = EditTool.Select;
    private Rgb _color = Rgb.Black;
    private double _fontSize = 12;
    private string? _fontFile;
    private string? _fontFamily;
    private bool _syncingFont;
    private readonly Dictionary<string, FontFamily> _fontCache = new();
    private PageEdit? _selectedEdit;

    // gesture state
    private Point _start;
    private Rectangle? _band;
    private InkEdit? _ink;
    private Polyline? _liveInk;
    private bool _moving;
    private Point _lastMove;

    // in-place text editing
    private TextEdit? _editingText;
    private TextBox? _textBox;

    public event Action? Closed;

    public PageEditor(StudioWindow host, Func<ActionContext> context, string work)
    {
        _host = host;
        _context = context;
        _work = work;
        Background = Ui.Background;
        Focusable = true; // takes keyboard focus back from form fields so the tool shortcuts work
        FocusVisualStyle = null;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(54) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- top bar ----
        var bar = new DockPanel { Background = Ui.Surface, LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        left.Children.Add(Ui.Btn(Ui.Glyph.Back, "All pages", Close, tooltip: "Back to all pages (Esc)"));
        left.Children.Add(Ui.Btn("", null, () => Go(-1), tooltip: "Previous page (Page Up)"));
        _pageLabel.Margin = new Thickness(6, 0, 6, 0);
        left.Children.Add(_pageLabel);
        left.Children.Add(Ui.Btn("", null, () => Go(1), tooltip: "Next page (Page Down)"));

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        right.Children.Add(Ui.Btn(Ui.Glyph.Undo, "Undo", Undo, tooltip: "Undo (Ctrl+Z)"));
        right.Children.Add(Separator());
        foreach (var (rgb, name) in new[] { (Rgb.Black, "Black"), (Rgb.Blue, "Blue"), (Rgb.Red, "Red") })
        {
            var swatch = Ui.Swatch(rgb, () => SetColor(rgb), name);
            _swatches[rgb] = swatch;
            right.Children.Add(swatch);
        }
        right.Children.Add(Separator());
        right.Children.Add(_fontBox);
        right.Children.Add(Ui.Btn(null, "A−", () => Resize(1 / 1.15), tooltip: "Smaller text / image"));
        _sizeLabel.Margin = new Thickness(4, 0, 4, 0);
        _sizeLabel.MinWidth = 22;
        _sizeLabel.TextAlignment = TextAlignment.Center;
        right.Children.Add(_sizeLabel);
        right.Children.Add(Ui.Btn(null, "A+", () => Resize(1.15), tooltip: "Bigger text / image"));
        right.Children.Add(Separator());
        right.Children.Add(Ui.Btn(Ui.Glyph.ZoomOut, null, () => SetZoom(_zoom / 1.2), tooltip: "Zoom out"));
        right.Children.Add(Ui.Btn(Ui.Glyph.ZoomIn, null, () => SetZoom(_zoom * 1.2), tooltip: "Zoom in"));
        right.Children.Add(Ui.Btn(null, "Fit", FitWidth, tooltip: "Fit page width"));

        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(left);
        bar.Children.Add(right);
        var barBorder = new Border { Child = bar, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        SetColumnSpan(barBorder, 2);
        Children.Add(barBorder);

        // ---- tool rail ----
        var rail = new StackPanel { Margin = new Thickness(8, 12, 8, 0) };
        AddTool(rail, EditTool.Select, Ui.Glyph.Select, "Select", "Select, move, delete (V)");
        AddTool(rail, EditTool.Text, Ui.Glyph.Font, "Text", "Click to type anywhere (T)");
        AddTool(rail, EditTool.Replace, Ui.Glyph.Replace, "Replace", "Drag a box over existing text to rewrite it (R)");
        AddTool(rail, EditTool.WhiteOut, Ui.Glyph.Eraser, "White-out", "Drag to cover something (W)");
        AddTool(rail, EditTool.Highlight, Ui.Glyph.Highlight, "Highlight", "Drag over text to highlight (H)");
        AddTool(rail, EditTool.Draw, Ui.Glyph.Pen, "Draw", "Draw freehand (D)");
        AddTool(rail, EditTool.Sign, Ui.Glyph.Sign, "Sign", "Place your saved signature (S). Right-click to redraw it.");
        _toolButtons[EditTool.Sign].MouseRightButtonUp += (_, e) => { e.Handled = true; RedrawSignature(); };
        AddTool(rail, EditTool.Image, Ui.Glyph.Picture, "Image", "Click to place a picture or logo (I)");
        var railBorder = new Border { Child = rail, Background = Ui.Surface, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) };
        SetRow(railBorder, 1);
        Children.Add(railBorder);

        // ---- page ----
        _pageHost.Children.Add(_pageImage);
        _pageHost.Children.Add(_layer);
        _pageHost.Children.Add(_fieldLayer);
        var paper = new Border
        {
            Child = _pageHost, Background = Brushes.White, BorderBrush = Ui.Line, BorderThickness = new Thickness(1),
            Margin = new Thickness(36), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        };
        _scroll = new ScrollViewer
        {
            Content = paper, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Ui.Background,
        };
        SetRow(_scroll, 1);
        SetColumn(_scroll, 1);
        Children.Add(_scroll);
        _scroll.SizeChanged += (_, _) => { if (_fitPending) FitWidth(); };
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
        };

        _layer.MouseLeftButtonDown += Layer_Down;
        _layer.MouseMove += Layer_Move;
        _layer.MouseLeftButtonUp += Layer_Up;

        SetTool(EditTool.Select);
        SetColor(Rgb.Black);

        _fontBox.Items.Add(new ComboBoxItem { Content = "Standard (Arial look)", Tag = null });
        _fontBox.SelectedIndex = 0;
        _fontBox.SelectionChanged += (_, _) => FontPicked();
        _ = LoadFontsAsync();
    }

    private async Task LoadFontsAsync()
    {
        try
        {
            var fonts = await Task.Run(FontCatalog.Installed);
            foreach (var f in fonts)
                _fontBox.Items.Add(new ComboBoxItem { Content = f.Family, Tag = f });
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't list fonts: " + ex.Message);
        }
    }

    // ---------- opening pages ----------

    public async Task OpenAsync(List<StudioPage> pages, int index)
    {
        CommitPending();
        _pages = pages;
        if (pages.Count == 0) { Close(); return; }
        _index = Math.Clamp(index, 0, pages.Count - 1);
        _page = pages[_index];
        _selectedEdit = null;
        _undo.Clear();
        _pageLabel.Text = $"Page {_index + 1} of {pages.Count}";

        var page = _page;
        _layer.Width = page.WidthPt;
        _layer.Height = page.HeightPt;
        _pageImage.Width = page.WidthPt;
        _pageImage.Height = page.HeightPt;
        _pageImage.Source = StudioWindow.LoadBitmap(page.ThumbPath); // blurry stand-in while the sharp one renders
        _fieldLayer.Width = page.WidthPt;
        _fieldLayer.Height = page.HeightPt;
        ApplyTransform();
        _fitPending = true;
        FitWidth();
        Rebuild();
        BuildFields();

        try
        {
            var ctx = _context();
            var png = System.IO.Path.Combine(_work, $"edit-{Guid.NewGuid():N}.png");
            await Task.Run(() => ctx.Renderer.RenderPageAsync(page.SourcePdf, page.SourceIndex, RenderDpi, png, ctx.Token));
            if (_page != page) return; // moved on while rendering
            _renderPng = png;
            _pageImage.Source = StudioWindow.LoadBitmap(png);
        }
        catch (Exception ex)
        {
            Log.Write("Editor render failed: " + ex);
            _host.SetStatus("Couldn't render this page sharply; showing the preview instead.");
        }
        _host.SetStatus(page.Fields.Count > 0 && _tool == EditTool.Select
            ? "This page has a form: click the blue boxes to fill them in."
            : HintFor(_tool));
    }

    private void Go(int delta)
    {
        if (_pages.Count == 0) return;
        int next = _index + delta;
        if (next < 0 || next >= _pages.Count) return;
        _ = OpenAsync(_pages, next);
    }

    private void Close()
    {
        CommitPending();
        _selectedEdit = null;
        Closed?.Invoke();
    }

    // ---------- view ----------

    private int Rotation => _page?.UserRotation ?? 0;

    private void ApplyTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new ScaleTransform(_zoom, _zoom));
        group.Children.Add(new RotateTransform(Rotation));
        _pageHost.LayoutTransform = group;
    }

    private void SetZoom(double zoom)
    {
        _fitPending = false;
        _zoom = Math.Clamp(zoom, 0.25, 4);
        ApplyTransform();
    }

    private void FitWidth()
    {
        if (_page == null) return;
        double available = _scroll.ActualWidth - 36 * 2 - 24;
        if (available <= 50) return; // not laid out yet; SizeChanged will call again
        _zoom = Math.Clamp(available / _page.DisplayWidth, 0.25, 3);
        ApplyTransform();
        _fitPending = true;
    }

    // ---------- tools and colours ----------

    private void AddTool(StackPanel rail, EditTool tool, string glyph, string label, string tip)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, FontSize = 11, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button
        {
            Content = content, Height = 58, Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            ToolTip = tip, FocusVisualStyle = null, Foreground = Ui.Text,
        };
        button.Click += (_, _) => SetTool(tool);
        _toolButtons[tool] = button;
        rail.Children.Add(button);
    }

    private void SetTool(EditTool tool)
    {
        CommitPending();
        _tool = tool;
        foreach (var (t, b) in _toolButtons)
        {
            b.Background = t == tool ? Ui.AccentSoft : Brushes.Transparent;
            b.BorderBrush = t == tool ? Ui.Accent : Brushes.Transparent;
        }
        _layer.Cursor = tool switch
        {
            EditTool.Text => Cursors.IBeam,
            EditTool.Draw => Cursors.Pen,
            EditTool.Select => Cursors.Arrow,
            EditTool.Image or EditTool.Sign => Cursors.Hand,
            _ => Cursors.Cross,
        };
        _fieldLayer.IsHitTestVisible = tool is EditTool.Select or EditTool.Text;
        _fieldLayer.Opacity = _fieldLayer.IsHitTestVisible ? 1 : 0.45;
        if (tool != EditTool.Select) Select(null);
        _host.SetStatus(HintFor(tool));
    }

    private static string HintFor(EditTool tool) => tool switch
    {
        EditTool.Text => "Click where you want to type. Esc when done.",
        EditTool.Replace => "Drag a box around the words you want to change. It reads them so you can retype.",
        EditTool.WhiteOut => "Drag over anything to cover it. The patch takes the colour of the page around it.",
        EditTool.Highlight => "Drag over text to highlight it.",
        EditTool.Draw => "Draw with the mouse, e.g. your signature. Pick a colour on the top right.",
        EditTool.Image => "Click where the picture should go, then pick the file.",
        EditTool.Sign => "Click where your signature goes. Right-click the Sign button to redraw it.",
        _ => "Click something you added to select it. Drag to move, Delete to remove, double-click text to retype.",
    };

    private void SetColor(Rgb color)
    {
        _color = color;
        foreach (var (rgb, swatch) in _swatches)
            swatch.BorderBrush = rgb == color ? Ui.Accent : Ui.Line;

        if (_selectedEdit is TextEdit t) { PushUndo(); t.Color = color; Rebuild(); }
        else if (_selectedEdit is InkEdit ink) { PushUndo(); ink.Color = color; Rebuild(); }
        else if (_selectedEdit is SignatureEdit sig) { PushUndo(); sig.Color = color; Rebuild(); }
        if (_textBox != null && _editingText != null)
        {
            _editingText.Color = color;
            _textBox.Foreground = Ui.Solid(color);
        }
    }

    private void Resize(double factor)
    {
        if (_selectedEdit is TextEdit t)
        {
            PushUndo();
            t.FontSize = Math.Clamp(Math.Round(t.FontSize * factor * 2) / 2, 5, 144);
            _fontSize = t.FontSize;
            Rebuild();
        }
        else if (_selectedEdit is ImageEdit im)
        {
            PushUndo();
            im.W *= factor;
            im.H *= factor;
            Rebuild();
        }
        else if (_selectedEdit is SignatureEdit sig)
        {
            PushUndo();
            sig.W *= factor;
            sig.H *= factor;
            Rebuild();
        }
        else if (_editingText != null && _textBox != null)
        {
            _editingText.FontSize = Math.Clamp(Math.Round(_editingText.FontSize * factor * 2) / 2, 5, 144);
            _fontSize = _editingText.FontSize;
            _textBox.FontSize = _fontSize;
        }
        else
        {
            _fontSize = Math.Clamp(Math.Round(_fontSize * factor * 2) / 2, 5, 144);
        }
        _sizeLabel.Text = _fontSize.ToString("0.#");
    }

    private static Border Separator() => new() { Width = 1, Height = 24, Background = Ui.Line, Margin = new Thickness(8, 0, 8, 0) };

    // ---------- undo ----------

    private void PushUndo()
    {
        if (_page == null) return;
        _undo.Push(_page.Edits.Select(e => e.Clone()).ToList());
    }

    private void Undo()
    {
        CommitPending();
        if (_page == null || _undo.Count == 0) { _host.SetStatus("Nothing to undo on this page."); return; }
        _page.Edits = _undo.Pop();
        Select(null);
        Rebuild();
    }

    // ---------- mouse ----------

    /// <summary>The angle that makes new text/images upright for the person looking at the rotated page.</summary>
    private int Upright => (360 - Rotation) % 360;

    private void Layer_Down(object sender, MouseButtonEventArgs e)
    {
        if (_page == null) return;
        var p = e.GetPosition(_layer);
        var hit = FindEdit(e.OriginalSource);

        // clicking outside the text box while typing finishes the text
        if (_textBox != null && !IsInside(e.OriginalSource, _textBox))
        {
            CommitPending();
            if (_tool == EditTool.Text) { e.Handled = true; return; }
        }
        if (_textBox == null) Focus();

        switch (_tool)
        {
            case EditTool.Select:
                Select(hit);
                if (hit is TextEdit text && e.ClickCount == 2) { PushUndo(); BeginTextEditing(text); break; }
                if (hit != null)
                {
                    PushUndo();
                    _moving = true;
                    _lastMove = p;
                    _layer.CaptureMouse();
                }
                break;

            case EditTool.Text:
                if (hit is TextEdit existing) { PushUndo(); BeginTextEditing(existing); break; }
                PushUndo();
                var created = new TextEdit { X = p.X, Y = p.Y, FontSize = _fontSize, Color = _color, Rotation = Upright, FontFile = _fontFile, FontFamily = _fontFamily };
                _page.Edits.Add(created);
                BeginTextEditing(created);
                break;

            case EditTool.Replace:
            case EditTool.WhiteOut:
            case EditTool.Highlight:
                _start = p;
                _band = new Rectangle
                {
                    Stroke = Ui.Accent, StrokeThickness = 1 / _zoom, StrokeDashArray = new DoubleCollection { 4, 3 },
                    Fill = _tool == EditTool.Highlight ? Ui.Solid(0x55, 0xFF, 0xE0, 0x3D) : Ui.Solid(0x22, 0x0E, 0x9F, 0x8B),
                };
                Canvas.SetLeft(_band, p.X);
                Canvas.SetTop(_band, p.Y);
                _layer.Children.Add(_band);
                _layer.CaptureMouse();
                break;

            case EditTool.Draw:
                PushUndo();
                _ink = new InkEdit { Color = _color, Width = 1.8 };
                _ink.Points.Add((p.X, p.Y));
                _liveInk = new Polyline
                {
                    Stroke = Ui.Solid(_color), StrokeThickness = _ink.Width, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                _liveInk.Points.Add(p);
                _layer.Children.Add(_liveInk);
                _layer.CaptureMouse();
                break;

            case EditTool.Image:
                PlaceImage(p);
                break;

            case EditTool.Sign:
                PlaceSignature(p);
                break;
        }
        e.Handled = true;
    }

    private void Layer_Move(object sender, MouseEventArgs e)
    {
        if (_page == null) return;
        var p = e.GetPosition(_layer);

        if (_moving && _selectedEdit != null && e.LeftButton == MouseButtonState.Pressed)
        {
            Move(_selectedEdit, p.X - _lastMove.X, p.Y - _lastMove.Y);
            _lastMove = p;
            Rebuild();
        }
        else if (_band != null)
        {
            Canvas.SetLeft(_band, Math.Min(p.X, _start.X));
            Canvas.SetTop(_band, Math.Min(p.Y, _start.Y));
            _band.Width = Math.Abs(p.X - _start.X);
            _band.Height = Math.Abs(p.Y - _start.Y);
        }
        else if (_ink != null && _liveInk != null)
        {
            var last = _ink.Points[^1];
            if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) < 0.6) return;
            _ink.Points.Add((p.X, p.Y));
            _liveInk.Points.Add(p);
        }
    }

    private void Layer_Up(object sender, MouseButtonEventArgs e)
    {
        if (_page == null) return;
        var p = e.GetPosition(_layer);
        _layer.ReleaseMouseCapture();

        if (_moving)
        {
            _moving = false;
            return;
        }

        if (_band != null)
        {
            _layer.Children.Remove(_band);
            _band = null;
            double x = Math.Min(p.X, _start.X), y = Math.Min(p.Y, _start.Y);
            double w = Math.Abs(p.X - _start.X), h = Math.Abs(p.Y - _start.Y);
            if (w < 3 || h < 3) return;

            if (_tool == EditTool.Replace)
            {
                _ = ReplaceTextAsync(x, y, w, h);
                return;
            }
            if (_tool == EditTool.WhiteOut)
            {
                _ = WhiteOutAsync(x, y, w, h);
                return;
            }
            PushUndo();
            _page.Edits.Add(new RectEdit { X = x, Y = y, W = w, H = h, Color = Rgb.Yellow, Opacity = 0.35 });
            Rebuild();
            return;
        }

        if (_ink != null)
        {
            if (_ink.Points.Count > 0) _page.Edits.Add(_ink);
            _ink = null;
            _liveInk = null;
            Rebuild();
        }
    }

    private static void Move(PageEdit edit, double dx, double dy)
    {
        switch (edit)
        {
            case TextEdit t: t.X += dx; t.Y += dy; break;
            case RectEdit r: r.X += dx; r.Y += dy; break;
            case ImageEdit i: i.X += dx; i.Y += dy; break;
            case SignatureEdit sig: sig.X += dx; sig.Y += dy; break;
            case InkEdit ink:
                for (int k = 0; k < ink.Points.Count; k++) ink.Points[k] = (ink.Points[k].X + dx, ink.Points[k].Y + dy);
                break;
        }
    }

    // ---------- white-out ----------

    /// <summary>Covers the box with the colour of the page around it, so it disappears on cream, grey or coloured pages too.</summary>
    private async Task WhiteOutAsync(double x, double y, double w, double h)
    {
        if (_page == null) return;
        var page = _page;
        var edit = new RectEdit { X = x, Y = y, W = w, H = h, Color = Rgb.White, Opacity = 1 };
        PushUndo();
        page.Edits.Add(edit);
        Rebuild(); // show it straight away, then correct the colour

        if (_renderPng == null) return;
        try
        {
            var ctx = _context();
            var png = _renderPng;
            var color = await Task.Run(() => StudioTools.SampleBackgroundAsync(ctx, png, RenderDpi, x, y, w, h));
            edit.Color = color;
            if (_page == page) Rebuild();
        }
        catch (Exception ex)
        {
            Log.Write("Background sampling failed: " + ex);
        }
    }

    // ---------- replace text ----------

    private async Task ReplaceTextAsync(double x, double y, double w, double h)
    {
        if (_page == null) return;
        var page = _page;
        string text = "";
        double size = Math.Clamp(h * 0.7, 6, 72);
        var background = Rgb.White;

        if (_renderPng != null)
        {
            _host.SetBusy("Reading the text...");
            try
            {
                var ctx = _context();
                var png = _renderPng;
                int rotation = Rotation;
                (text, size, background) = await Task.Run(() => StudioTools.ReadRegionAsync(ctx, png, RenderDpi, x, y, w, h, rotation));
            }
            catch (Exception ex)
            {
                Log.Write("Replace text read failed: " + ex);
                _host.SetStatus("Couldn't read that text, type the new text instead.");
            }
            finally
            {
                _host.ClearBusy();
            }
        }
        if (_page != page) return;

        PushUndo();
        page.Edits.Add(new RectEdit { X = x, Y = y, W = w, H = h, Color = background, Opacity = 1 });

        // start typing at the box's top-left as the person sees it (the page may be rotated)
        (double ax, double ay) = Rotation switch
        {
            90 => (x + 1, y + h - 1),
            180 => (x + w - 1, y + h - 1),
            270 => (x + w - 1, y + 1),
            _ => (x + 1, y + 1),
        };
        var replacement = new TextEdit { X = ax, Y = ay, Text = text, FontSize = size, Color = _color, Rotation = Upright, FontFile = _fontFile, FontFamily = _fontFamily };
        page.Edits.Add(replacement);
        BeginTextEditing(replacement);
        _host.SetStatus(text.Length > 0 ? "Edit the text, then press Esc or click elsewhere." : "No text found there. Type the new text.");
    }

    // ---------- images ----------

    private void PlaceImage(Point p)
    {
        if (_page == null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic|All files|*.*",
            Title = "Pick a picture, logo or signature",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var bmp = StudioWindow.LoadBitmap(dialog.FileName);
        double aspect = bmp != null && bmp.PixelWidth > 0 ? (double)bmp.PixelHeight / bmp.PixelWidth : 1;
        double w = Math.Min(160, _page.DisplayWidth * 0.4);
        PushUndo();
        var image = new ImageEdit { X = p.X, Y = p.Y, W = w, H = w * aspect, ImagePath = dialog.FileName, Rotation = Upright };
        _page.Edits.Add(image);
        SetTool(EditTool.Select);
        Select(image);
        Rebuild();
        _host.SetStatus("Drag the picture to move it. A− / A+ resize it.");
    }

    // ---------- signature ----------

    private void PlaceSignature(Point p)
    {
        if (_page == null) return;
        var signature = SavedSignature.Load() ?? Dialogs.DrawSignature(Window.GetWindow(this));
        if (signature == null) { SetTool(EditTool.Select); return; }
        PushUndo();
        var edit = signature.Place(p.X, p.Y, Math.Min(150, _page.DisplayWidth * 0.3), Upright, _color);
        _page.Edits.Add(edit);
        SetTool(EditTool.Select);
        Select(edit);
        _host.SetStatus("Drag the signature to move it. A− / A+ resize it, the colour dots recolour it.");
    }

    private void RedrawSignature()
    {
        var signature = Dialogs.DrawSignature(Window.GetWindow(this));
        if (signature == null) return;
        _host.SetStatus("Signature saved. Click on the page to place it.");
        SetTool(EditTool.Sign);
    }

    // ---------- fonts ----------

    private FontFamily FontFor(TextEdit t)
    {
        if (string.IsNullOrEmpty(t.FontFamily)) return Ui.PageText;
        if (!_fontCache.TryGetValue(t.FontFamily, out var family))
            _fontCache[t.FontFamily] = family = new FontFamily(t.FontFamily);
        return family;
    }

    /// <summary>Shows the text's font in the list without changing anything.</summary>
    private void ShowFont(TextEdit t)
    {
        _syncingFont = true;
        try
        {
            int index = 0;
            for (int i = 1; i < _fontBox.Items.Count; i++)
            {
                if (_fontBox.Items[i] is ComboBoxItem { Tag: FontCatalog.Entry e }
                    && string.Equals(e.Path, t.FontFile, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
            }
            _fontBox.SelectedIndex = index;
            _fontFile = t.FontFile;
            _fontFamily = t.FontFamily;
        }
        finally
        {
            _syncingFont = false;
        }
    }

    private void FontPicked()
    {
        if (_syncingFont) return;
        var entry = (_fontBox.SelectedItem as ComboBoxItem)?.Tag as FontCatalog.Entry;
        _fontFile = entry?.Path;
        _fontFamily = entry?.Family;

        if (_editingText != null && _textBox != null)
        {
            _editingText.FontFile = _fontFile;
            _editingText.FontFamily = _fontFamily;
            _textBox.FontFamily = FontFor(_editingText);
            var box = _textBox;
            Dispatcher.BeginInvoke(new Action(() => { if (_textBox == box) box.Focus(); }));
            return;
        }
        if (_selectedEdit is TextEdit t)
        {
            PushUndo();
            t.FontFile = _fontFile;
            t.FontFamily = _fontFamily;
            Rebuild();
        }
        Dispatcher.BeginInvoke(new Action(() => Focus()));
    }

    // ---------- form fields ----------

    private static readonly Brush FieldFill = Ui.Solid(0x66, 0xDC, 0xE6, 0xFF);

    /// <summary>The PDF's own fillable boxes as real controls you can click and type into.</summary>
    private void BuildFields()
    {
        _fieldLayer.Children.Clear();
        if (_page == null) return;
        foreach (var f in _page.Fields)
        {
            FrameworkElement control = f.Kind switch
            {
                FieldKind.Checkbox => Toggle(f, round: false),
                FieldKind.Radio => Toggle(f, round: true),
                FieldKind.Choice => Choice(f),
                _ => TextField(f),
            };
            control.Width = Math.Max(4, f.W);
            control.Height = Math.Max(4, f.H);
            control.ToolTip = f.Label;
            Canvas.SetLeft(control, f.X);
            Canvas.SetTop(control, f.Y);
            _fieldLayer.Children.Add(control);
        }
    }

    private static TextBox TextField(FormField f)
    {
        var box = new TextBox
        {
            Text = f.Value, FontFamily = Ui.PageText, Background = FieldFill, BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0, 2, 0), Foreground = Brushes.Black,
            FontSize = f.Multiline ? 10 : Math.Clamp(f.H * 0.62, 6, 14),
            AcceptsReturn = f.Multiline, TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = f.Multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        box.TextChanged += (_, _) => f.Value = box.Text.Replace("\r\n", "\n");
        return box;
    }

    private static ComboBox Choice(FormField f)
    {
        var combo = new ComboBox { Background = FieldFill, FontSize = Math.Clamp(f.H * 0.55, 6, 13), Padding = new Thickness(2, 0, 2, 0) };
        foreach (var c in f.Choices) combo.Items.Add(c);
        if (f.Value.Length > 0 && !f.Choices.Contains(f.Value)) combo.Items.Add(f.Value);
        combo.SelectedItem = f.Value.Length > 0 ? f.Value : null;
        combo.SelectionChanged += (_, _) => f.Value = combo.SelectedItem as string ?? "";
        return combo;
    }

    private Border Toggle(FormField f, bool round)
    {
        double size = Math.Min(f.W, f.H);
        var mark = new TextBlock
        {
            Text = round ? "\u25CF" : "\u2713", FontFamily = new FontFamily("Segoe UI Symbol"), Foreground = Brushes.Black,
            FontSize = Math.Max(5, size * (round ? 0.6 : 0.85)), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Visibility = f.Value == "1" ? Visibility.Visible : Visibility.Hidden,
        };
        var border = new Border
        {
            Background = FieldFill, Child = mark, Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(round ? size / 2 : 0),
        };
        border.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!round)
            {
                f.Value = f.Value == "1" ? "" : "1";
                mark.Visibility = f.Value == "1" ? Visibility.Visible : Visibility.Hidden;
                return;
            }
            // one choice per group: clear the others with the same name in the same file
            var source = _page?.SourcePdf;
            foreach (var other in _pages.Where(p => p.SourcePdf == source).SelectMany(p => p.Fields))
                if (other != f && other.Kind == FieldKind.Radio && other.Name == f.Name) other.Value = "";
            f.Value = "1";
            BuildFields();
        };
        return border;
    }

    // ---------- text editing ----------

    private void BeginTextEditing(TextEdit text)
    {
        CommitPending();
        _editingText = text;
        ShowFont(text);
        _fontSize = text.FontSize;
        _sizeLabel.Text = _fontSize.ToString("0.#");
        Rebuild(); // hides the static version while typing

        var box = new TextBox
        {
            Text = text.Text, FontFamily = FontFor(text), FontSize = text.FontSize, Foreground = Ui.Solid(text.Color),
            Background = Ui.Solid(0x18, 0x0E, 0x9F, 0x8B), BorderBrush = Ui.Accent, BorderThickness = new Thickness(1 / _zoom),
            Padding = new Thickness(0), Margin = new Thickness(-3, -1, 0, 0), MinWidth = 30, AcceptsReturn = true,
            RenderTransform = new RotateTransform(text.Rotation),
        };
        Canvas.SetLeft(box, text.X);
        Canvas.SetTop(box, text.Y);
        _layer.Children.Add(box);
        _textBox = box;
        box.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; if (box.Text.Length > 0) box.SelectAll(); };
        box.LostKeyboardFocus += (_, e) =>
        {
            // picking a font keeps the text open so the font applies to it
            bool toFontList = e.NewFocus == _fontBox || (e.NewFocus is ComboBoxItem item && _fontBox.Items.Contains(item));
            if (_textBox == box && !toFontList) CommitPending();
        };
    }

    /// <summary>Finishes any text being typed. Safe to call any time.</summary>
    public void CommitPending()
    {
        if (_textBox == null || _editingText == null) return;
        var text = _editingText;
        text.Text = _textBox.Text.Replace("\r\n", "\n").TrimEnd();
        _layer.Children.Remove(_textBox);
        _textBox = null;
        _editingText = null;
        if (string.IsNullOrWhiteSpace(text.Text)) _page?.Edits.Remove(text);
        Rebuild();
    }

    // ---------- keys (forwarded by the window) ----------

    public void HandleKey(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (_textBox != null)
        {
            if (e.Key == Key.Escape) { e.Handled = true; CommitPending(); }
            return; // everything else goes to the text box
        }
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem)
        {
            // typing into a form field or the font list
            if (e.Key == Key.Escape) { e.Handled = true; Focus(); }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_selectedEdit != null) Select(null); else Close();
                break;
            case Key.Delete:
            case Key.Back:
                e.Handled = true;
                if (_selectedEdit != null && _page != null)
                {
                    PushUndo();
                    _page.Edits.Remove(_selectedEdit);
                    Select(null);
                    Rebuild();
                }
                break;
            case Key.Z when ctrl: e.Handled = true; Undo(); break;
            case Key.PageUp: e.Handled = true; Go(-1); break;
            case Key.PageDown: e.Handled = true; Go(1); break;
            case Key.V when !ctrl: e.Handled = true; SetTool(EditTool.Select); break;
            case Key.T when !ctrl: e.Handled = true; SetTool(EditTool.Text); break;
            case Key.R when !ctrl: e.Handled = true; SetTool(EditTool.Replace); break;
            case Key.W when !ctrl: e.Handled = true; SetTool(EditTool.WhiteOut); break;
            case Key.H when !ctrl: e.Handled = true; SetTool(EditTool.Highlight); break;
            case Key.D when !ctrl: e.Handled = true; SetTool(EditTool.Draw); break;
            case Key.I when !ctrl: e.Handled = true; SetTool(EditTool.Image); break;
            case Key.S when !ctrl: e.Handled = true; SetTool(EditTool.Sign); break;
        }
    }

    // ---------- drawing the edits ----------

    private void Select(PageEdit? edit)
    {
        _selectedEdit = edit;
        if (edit is TextEdit t)
        {
            _fontSize = t.FontSize;
            _sizeLabel.Text = _fontSize.ToString("0.#");
            ShowFont(t);
        }
        Rebuild();
    }

    private void Rebuild()
    {
        // keep the text box (and live gestures) while rebuilding everything else
        var keep = new List<UIElement>();
        if (_textBox != null) keep.Add(_textBox);
        if (_band != null) keep.Add(_band);
        if (_liveInk != null) keep.Add(_liveInk);
        _layer.Children.Clear();
        if (_page == null) return;

        foreach (var edit in _page.Edits)
        {
            if (edit == _editingText) continue;
            var element = Visual(edit);
            element.Tag = edit;
            _layer.Children.Add(element);
            if (edit == _selectedEdit) _layer.Children.Add(SelectionOutline(edit, element));
        }
        foreach (var k in keep) _layer.Children.Add(k);
    }

    private FrameworkElement Visual(PageEdit edit)
    {
        switch (edit)
        {
            case RectEdit r:
            {
                // no outline: a cover patch should vanish into the page (select it with the Select tool to see its box)
                var rect = new Rectangle { Width = r.W, Height = r.H, Fill = Ui.Solid(r.Color), Opacity = r.Opacity };
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                return rect;
            }
            case TextEdit t:
            {
                var tb = new TextBlock
                {
                    Text = t.Text, FontFamily = FontFor(t), FontSize = t.FontSize, Foreground = Ui.Solid(t.Color),
                    RenderTransform = new RotateTransform(t.Rotation), Background = Brushes.Transparent,
                };
                Canvas.SetLeft(tb, t.X);
                Canvas.SetTop(tb, t.Y);
                return tb;
            }
            case InkEdit ink:
            {
                var line = new Polyline
                {
                    Stroke = Ui.Solid(ink.Color), StrokeThickness = ink.Width, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                foreach (var (x, y) in ink.Points) line.Points.Add(new Point(x, y));
                return line;
            }
            case ImageEdit im:
            {
                var img = new Image
                {
                    Source = StudioWindow.LoadBitmap(im.ImagePath), Width = im.W, Height = im.H, Stretch = Stretch.Fill,
                    RenderTransform = new RotateTransform(im.Rotation),
                };
                Canvas.SetLeft(img, im.X);
                Canvas.SetTop(img, im.Y);
                return img;
            }
            case SignatureEdit sig:
            {
                var canvas = new Canvas
                {
                    Width = sig.W, Height = sig.H, Background = Brushes.Transparent,
                    RenderTransform = new RotateTransform(sig.Rotation),
                };
                foreach (var stroke in sig.Strokes)
                {
                    var line = new Polyline
                    {
                        Stroke = Ui.Solid(sig.Color), StrokeThickness = sig.PenWidth, StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    };
                    foreach (var (x, y) in stroke) line.Points.Add(new Point(x * sig.W, y * sig.H));
                    canvas.Children.Add(line);
                }
                Canvas.SetLeft(canvas, sig.X);
                Canvas.SetTop(canvas, sig.Y);
                return canvas;
            }
            default:
                return new Border();
        }
    }

    /// <summary>Dashed box around the selected item (accounts for rotated text and images).</summary>
    private Rectangle SelectionOutline(PageEdit edit, FrameworkElement element)
    {
        double x, y, w, h;
        switch (edit)
        {
            case InkEdit ink when ink.Points.Count > 0:
                x = ink.Points.Min(p => p.X) - 3; y = ink.Points.Min(p => p.Y) - 3;
                w = ink.Points.Max(p => p.X) - x + 3; h = ink.Points.Max(p => p.Y) - y + 3;
                break;
            case RectEdit r:
                (x, y, w, h) = (r.X - 2, r.Y - 2, r.W + 4, r.H + 4);
                break;
            default:
            {
                element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                (double ox, double oy, double ew, double eh, int rot) = edit switch
                {
                    ImageEdit im => (im.X, im.Y, im.W, im.H, im.Rotation),
                    SignatureEdit sig => (sig.X, sig.Y, sig.W, sig.H, sig.Rotation),
                    TextEdit t => (t.X, t.Y, element.DesiredSize.Width, element.DesiredSize.Height, t.Rotation),
                    _ => (0.0, 0.0, element.DesiredSize.Width, element.DesiredSize.Height, 0),
                };
                double rad = rot * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
                var corners = new[] { (0.0, 0.0), (ew, 0.0), (0.0, eh), (ew, eh) }
                    .Select(c => (X: ox + c.Item1 * cos - c.Item2 * sin, Y: oy + c.Item1 * sin + c.Item2 * cos)).ToList();
                x = corners.Min(c => c.X) - 2; y = corners.Min(c => c.Y) - 2;
                w = corners.Max(c => c.X) - x + 2; h = corners.Max(c => c.Y) - y + 2;
                break;
            }
        }
        var outline = new Rectangle
        {
            Width = Math.Max(1, w), Height = Math.Max(1, h), Stroke = Ui.Accent, StrokeThickness = 1.2 / _zoom,
            StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false,
        };
        Canvas.SetLeft(outline, x);
        Canvas.SetTop(outline, y);
        return outline;
    }

    private static PageEdit? FindEdit(object source)
    {
        DependencyObject? node = source as DependencyObject;
        while (node != null)
        {
            if (node is FrameworkElement fe && fe.Tag is PageEdit edit) return edit;
            if (node is Canvas) return null;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private static bool IsInside(object source, DependencyObject container)
    {
        DependencyObject? node = source as DependencyObject;
        while (node != null)
        {
            if (node == container) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
