using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Droppo.Core;
using Droppo.Shell.Studio;

namespace Droppo.Shell;

/// <summary>
/// Droppo's main window: a sidebar of tool groups, a big drop zone, and tool cards.
/// Drop (or browse to) files and it shows every action for them; click one to run it.
/// The Shift-drag wheel does the same thing from anywhere in Windows.
/// </summary>
internal sealed class HomeWindow : GlassWindow
{
    private enum Page { Home, Convert, Compress, Pdf, Organise, Tools }

    private static readonly (Page Page, string Glyph, string Label, string Blurb, string Lead)[] Pages =
    {
        (Page.Home, "", "Home", "", "Drop a file to see everything Droppo can do with it."),
        (Page.Convert, "", "Convert", "Images, audio, video", "Turn files into the format you need."),
        (Page.Compress, "", "Compress", "Smaller file sizes", "Make files smaller, to a size you pick."),
        (Page.Pdf, "", "PDF", "Edit, merge, split", "Arrange, edit, sign and protect PDFs."),
        (Page.Organise, "", "Organise", "Rename, zip, join", "Tidy up many files at once."),
        (Page.Tools, "", "Tools", "Trim, rotate, more", "Little fixes for pictures, video and audio."),
    };

    // what each group can do, shown before any file is picked
    private static readonly Dictionary<Page, (string Glyph, string Title, string Text)[]> Catalog = new()
    {
        [Page.Convert] = new[]
        {
            ("", "Pictures", "PNG, JPG, WebP, iPhone HEIC, or a PDF"),
            ("", "Video", "MP4 that plays anywhere, GIF, or just the sound"),
            ("", "Audio", "MP3, WAV, M4A or FLAC"),
            ("", "Documents", "Word, PowerPoint and Excel to PDF"),
        },
        [Page.Compress] = new[]
        {
            ("", "Smaller", "Pick a % of the original or an exact size in MB"),
            ("", "Resize", "Passport photo, Instagram, Full HD or your own size"),
        },
        [Page.Pdf] = new[]
        {
            ("", "PDF Studio", "Reorder, rotate, delete, edit text, sign, fill forms"),
            ("", "Merge and split", "Join PDFs in your order, or one file per page"),
            ("", "Password", "Lock a PDF, or unlock one you have the password for"),
            ("", "Copy text", "Gets the text out, even from scans"),
        },
        [Page.Organise] = new[]
        {
            ("", "Batch rename", "Photo 001, Photo 002... with a live preview and undo"),
            ("", "ZIP", "Pack many files into one"),
            ("", "Join", "Clips or recordings into one, in your order"),
            ("", "Copy paths", "Full paths or just the names, ready to paste"),
        },
        [Page.Tools] = new[]
        {
            ("", "Trim", "Cut the start and end of a video or recording"),
            ("", "Rotate video", "Fix phone videos filmed sideways"),
            ("", "Remove background", "Logos and product shots on plain backgrounds"),
            ("", "Strip location", "Remove GPS and camera details from photos"),
            ("", "Text from pictures", "Copy the words from a photo or screenshot"),
            ("", "Snapshot", "Save a frame from a video as a picture"),
        },
    };

    private readonly Func<Settings> _settings;
    private readonly Dictionary<Page, Border> _nav = new();
    private readonly StackPanel _content = new() { Margin = new Thickness(36, 30, 36, 30), MaxWidth = 900 };
    private readonly ScrollViewer _scroll;
    private Page _page = Page.Home;
    private IReadOnlyList<string>? _files;
    private Border? _dropZone;
    private System.Windows.Shapes.Rectangle? _dropOutline;

    public event Action<WheelAction, IReadOnlyList<string>>? ActionChosen;
    public event Action? StudioRequested;
    public event Action? SettingsRequested;

    public HomeWindow(Func<Settings> settings) : base(card: false, overlayCaption: true)
    {
        _settings = settings;
        Title = "Droppo";
        Width = 1040;
        Height = 680;
        MinWidth = 820;
        MinHeight = 540;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowDrop = true;

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(Sidebar());

        _scroll = new ScrollViewer { Content = _content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(_scroll, 1);
        root.Children.Add(_scroll);
        Content = root;

        DragEnter += (_, e) => { e.Effects = Files(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; HighlightDrop(true); e.Handled = true; };
        DragOver += (_, e) => { e.Effects = Files(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragLeave += (_, _) => HighlightDrop(false);
        Drop += (_, e) =>
        {
            HighlightDrop(false);
            if (Files(e.Data) is { } files) { e.Effects = DragDropEffects.Copy; ShowFiles(files); }
            e.Handled = true;
        };
        Show(Page.Home);
    }

    // ---------- sidebar ----------

    private UIElement Sidebar()
    {
        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(16, 18, 16, 18) };

        var logo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 28) };
        var tile = Brand.Tile(30);
        logo.Children.Add(tile);
        var word = Brand.Wordmark(15, Brand.Brush(Brand.Navy));
        word.Margin = new Thickness(10, 6, 0, 0);
        logo.Children.Add(word);
        DockPanel.SetDock(logo, Dock.Top);
        dock.Children.Add(logo);

        foreach (var p in Pages)
        {
            var item = NavItem(p.Glyph, p.Label, () => Show(p.Page));
            _nav[p.Page] = item;
            DockPanel.SetDock(item, Dock.Top);
            dock.Children.Add(item);
        }

        var studio = NavItem("", "PDF Studio", () => StudioRequested?.Invoke());
        studio.Margin = new Thickness(0, 14, 0, 2);
        DockPanel.SetDock(studio, Dock.Top);
        dock.Children.Add(studio);

        var settings = NavItem("", "Settings", () => SettingsRequested?.Invoke());
        DockPanel.SetDock(settings, Dock.Bottom);
        dock.Children.Add(settings);

        return new Border
        {
            Child = dock, Background = Glass.BarBackground,
            BorderBrush = Glass.Enabled ? Brand.Brush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) : Ui.Line,
            BorderThickness = new Thickness(0, 0, 1, 0),
        };
    }

    private static Border NavItem(string glyph, string label, Action click)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 16, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Width = 22 });
        row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, Foreground = Ui.Text, Margin = new Thickness(10, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var item = new Border
        {
            Child = row, Height = 40, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 0, 2),
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        item.MouseEnter += (_, _) => { if (!Equals(item.Tag, true)) item.Background = Ui.Hover; };
        item.MouseLeave += (_, _) => { if (!Equals(item.Tag, true)) item.Background = Brushes.Transparent; };
        item.MouseLeftButtonUp += (_, _) => click();
        return item;
    }

    private static void SetNav(Border item, bool on)
    {
        item.Tag = on;
        item.Background = on ? Ui.AccentSoft : Brushes.Transparent;
        if (item.Child is StackPanel row)
        {
            ((TextBlock)row.Children[0]).Foreground = on ? Ui.Accent : Ui.Muted;
            var text = (TextBlock)row.Children[1];
            text.Foreground = on ? Ui.Accent : Ui.Text;
            text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---------- pages ----------

    private void Show(Page page)
    {
        _page = page;
        foreach (var (p, item) in _nav) SetNav(item, p == page);
        Render();
    }

    /// <summary>Shows what can be done with these files (also used by the preview pictures).</summary>
    public void ShowFiles(IReadOnlyList<string> files)
    {
        _files = files.Count > 0 ? files : null;
        Render();
        Activate();
    }

    private void Render()
    {
        _content.Children.Clear();
        _dropZone = null;
        var info = Pages.First(p => p.Page == _page);

        if (_files != null)
        {
            RenderActions(_files);
        }
        else
        {
            Header(_page == Page.Home ? "Welcome to Droppo" : info.Label, info.Lead);
            if (_page == Page.Pdf)
            {
                var studio = Ui.Btn(Ui.Glyph.Edit, "Open PDF Studio", () => StudioRequested?.Invoke(), primary: true);
                studio.HorizontalAlignment = HorizontalAlignment.Left;
                studio.Margin = new Thickness(0, 0, 0, 18);
                _content.Children.Add(studio);
            }
            _content.Children.Add(DropZone(_page == Page.Home ? "Drop your files here" : $"Drop files to {info.Label.ToLowerInvariant()}"));
            if (_page == Page.Home) RenderToolCards();
            else RenderCatalog(_page);
            _content.Children.Add(Tip());
        }
        Motion.Rise(_content, 6, 140);
        _scroll.ScrollToVerticalOffset(0);
    }

    private void Header(string title, string lead)
    {
        var t = Ui.Txt(title, 24, bold: true);
        t.Margin = new Thickness(0, 0, 0, 4);
        _content.Children.Add(t);
        var l = Ui.Txt(lead, 13.5, color: Ui.Muted);
        l.TextWrapping = TextWrapping.Wrap;
        l.TextTrimming = TextTrimming.None;
        l.Margin = new Thickness(0, 0, 0, 20);
        _content.Children.Add(l);
    }

    private Border DropZone(string headline)
    {
        var icon = new Border
        {
            Width = 60, Height = 60, CornerRadius = new CornerRadius(18), Background = Ui.AccentSoft,
            HorizontalAlignment = HorizontalAlignment.Center, Child = Brand.Symbol(28, Ui.Accent),
        };
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(icon);
        var line = Ui.Txt(headline, 17, bold: true);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.Margin = new Thickness(0, 14, 0, 3);
        stack.Children.Add(line);
        var sub = new TextBlock { FontSize = 13, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center };
        sub.Inlines.Add(new System.Windows.Documents.Run("or "));
        sub.Inlines.Add(new System.Windows.Documents.Run("click to browse") { Foreground = Ui.Accent, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(sub);

        _dropOutline = new System.Windows.Shapes.Rectangle
        {
            RadiusX = 12, RadiusY = 12, Margin = new Thickness(10), StrokeThickness = 1.5, IsHitTestVisible = false,
            Stroke = Brand.Brush(Color.FromArgb(0x80, 0x38, 0x82, 0xF6)), StrokeDashArray = new DoubleCollection { 5, 4 },
        };
        var inner = new Grid { Height = 210 };
        inner.Children.Add(_dropOutline);
        inner.Children.Add(stack);
        var zone = Glass.Card(inner, new Thickness(0), 16);
        zone.Cursor = Cursors.Hand;
        zone.Margin = new Thickness(0, 0, 0, 26);
        zone.MouseLeftButtonUp += (_, _) => Browse();
        _dropZone = zone;
        return zone;
    }

    private void HighlightDrop(bool on)
    {
        if (_dropZone == null || _dropOutline == null) return;
        _dropZone.Background = on ? Ui.AccentSoft : Glass.CardBackground;
        _dropOutline.Stroke = on ? Ui.Accent : Brand.Brush(Color.FromArgb(0x80, 0x38, 0x82, 0xF6));
    }

    private void RenderToolCards()
    {
        var grid = new WrapPanel();
        foreach (var p in Pages.Where(p => p.Page != Page.Home))
        {
            var card = Card(p.Glyph, p.Label, p.Blurb, () => Show(p.Page), width: 252);
            grid.Children.Add(card);
        }
        _content.Children.Add(grid);
    }

    private void RenderCatalog(Page page)
    {
        var title = Ui.Txt("What it can do", 13, bold: true, color: Ui.Muted);
        title.Margin = new Thickness(2, 0, 0, 10);
        _content.Children.Add(title);
        var grid = new WrapPanel();
        foreach (var (glyph, name, text) in Catalog[page])
            grid.Children.Add(Card(glyph, name, text, Browse, width: 252));
        _content.Children.Add(grid);
    }

    private void RenderActions(IReadOnlyList<string> files)
    {
        var actions = ActionRegistry.For(files, _settings());
        var names = files.Count == 1 ? Path.GetFileName(files[0].TrimEnd('\\', '/')) : FileKinds.Describe(files);
        Header("What should we do?", files.Count == 1 ? names : $"{names}: {string.Join(", ", files.Take(3).Select(f => Path.GetFileName(f.TrimEnd('\\', '/'))))}{(files.Count > 3 ? "..." : "")}");

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, -6, 0, 22) };
        bar.Children.Add(Ui.Btn(Ui.Glyph.Add, "Choose other files", Browse));
        bar.Children.Add(Ui.Btn(null, "Clear", () => { _files = null; Render(); }));
        _content.Children.Add(bar);

        if (actions.Count == 0)
        {
            _content.Children.Add(Ui.Txt("Droppo doesn't have anything for these files yet.", 14, color: Ui.Muted));
            return;
        }

        var groups = actions.GroupBy(GroupOf).OrderBy(g => g.Key).ToList();
        foreach (var group in groups)
        {
            if (_page != Page.Home && group.Key != _page) continue;
            var info = Pages.First(p => p.Page == group.Key);
            var heading = Ui.Txt(info.Label, 13, bold: true, color: Ui.Muted);
            heading.Margin = new Thickness(2, 0, 0, 10);
            _content.Children.Add(heading);
            var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
            foreach (var action in group)
            {
                var a = action;
                wrap.Children.Add(Card(GlyphOf(a), a.Label, a.Hint, () => ActionChosen?.Invoke(a, files), width: 200));
            }
            _content.Children.Add(wrap);
        }
        if (_page != Page.Home && groups.All(g => g.Key != _page))
            _content.Children.Add(Ui.Txt($"Nothing under {Pages.First(p => p.Page == _page).Label} for these files. Try Home to see everything.", 13.5, color: Ui.Muted));
    }

    /// <summary>A white glass card: icon on an ice-blue tile, a title, a line of detail.</summary>
    private static Border Card(string glyph, string title, string detail, Action click, double width)
    {
        var icon = new Border
        {
            Width = 38, Height = 38, CornerRadius = new CornerRadius(11), Background = Ui.AccentSoft, HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = glyph, FontFamily = Ui.Icons, FontSize = 17, Foreground = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var stack = new StackPanel();
        stack.Children.Add(icon);
        var t = Ui.Txt(title, 14, bold: true);
        t.Margin = new Thickness(0, 12, 0, 2);
        stack.Children.Add(t);
        var d = Ui.Txt(detail, 12, color: Ui.Muted);
        d.TextWrapping = TextWrapping.Wrap;
        d.TextTrimming = TextTrimming.None;
        stack.Children.Add(d);
        var card = Glass.Card(stack, new Thickness(16, 16, 16, 16), 14);
        card.Width = width;
        card.MinHeight = 128;
        card.Margin = new Thickness(0, 0, 14, 14);
        card.Cursor = Cursors.Hand;
        var rest = card.BorderBrush;
        card.MouseEnter += (_, _) => card.BorderBrush = Ui.Accent;
        card.MouseLeave += (_, _) => card.BorderBrush = rest;
        card.MouseLeftButtonUp += (_, _) => click();
        return card;
    }

    private UIElement Tip()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 12, 0, 0) };
        row.Children.Add(new TextBlock { Text = "", FontFamily = Ui.Icons, FontSize = 13, Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center });
        var text = Ui.Txt($"Tip: anywhere in Windows, start dragging a file and hold {_settings().TriggerKey} to open the Droppo wheel.", 12.5, color: Ui.Muted);
        text.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(text);
        return row;
    }

    // ---------- files ----------

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files for Droppo", Filter = "All files|*.*" };
        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0) ShowFiles(dialog.FileNames);
    }

    private static IReadOnlyList<string>? Files(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) return paths;
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't read dropped files: " + ex.Message);
        }
        return null;
    }

    // ---------- which group and icon each action gets ----------

    private static Page GroupOf(WheelAction a)
    {
        string id = a.Id;
        if (id.EndsWith(".smaller", StringComparison.Ordinal) || id == "img.resize") return Page.Compress;
        if (id.Contains("studio", StringComparison.Ordinal) || id is "pdf.merge" or "pdf.split" or "pdf.lock" or "pdf.text" or "pdf.rotate") return Page.Pdf;
        if (id is "any.rename" or "any.zip" or "any.path" or "any.name" or "vid.join" or "aud.join") return Page.Organise;
        if (id is "img.png" or "img.jpg" or "img.webp" or "img.pdf" or "vid.mp4" or "vid.gif" or "vid.mp3" or "aud.mp3" or "aud.wav" or "aud.m4a"
            or "aud.flac" or "doc.pdf" or "doc.combine" or "any.pdf" or "pdf.jpg" or "pdf.png") return Page.Convert;
        return Page.Tools;
    }

    private static string GlyphOf(WheelAction a) => a.Id switch
    {
        "img.png" or "img.jpg" or "img.webp" or "pdf.jpg" or "pdf.png" => "",
        "vid.mp4" or "vid.gif" => "",
        "vid.mp3" or "aud.mp3" or "aud.wav" or "aud.m4a" or "aud.flac" => "",
        "img.pdf" or "doc.pdf" or "doc.combine" or "any.pdf" or "pdf.merge" or "pdf.split" => "",
        "pdf.lock" => "",
        "pdf.text" or "img.text" => "",
        "pdf.rotate" or "vid.rotate" => "",
        "img.resize" => "",
        "img.clean" => "",
        "img.nobg" => "",
        "vid.trim" or "aud.trim" => "",
        "vid.mute" => "",
        "vid.frame" => "",
        "aud.loud" => "",
        "vid.join" or "aud.join" => "",
        "any.rename" => "",
        "any.zip" => "",
        "any.path" or "any.name" => "",
        _ when a.Id.EndsWith(".smaller", StringComparison.Ordinal) => "",
        _ when a.Id.Contains("studio", StringComparison.Ordinal) => "",
        _ => "",
    };
}
