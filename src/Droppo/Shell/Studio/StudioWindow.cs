using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Droppo.Core;
using Droppo.Core.Studio;

namespace Droppo.Shell.Studio;

/// <summary>
/// PDF Studio: every page as a thumbnail. Drag to reorder, select to rotate/delete/extract,
/// drop more files in to merge, double-click a page to edit it. Nothing touches the originals;
/// Save writes a new file next to them.
/// </summary>
internal sealed class StudioWindow : GlassWindow
{
    private const string InternalDragFormat = "Droppo.StudioPages";
    private const string FileFilter = "PDFs, images and Office files|*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.heic;*.bmp;*.gif;*.tif;*.tiff;*.doc;*.docx;*.rtf;*.odt;*.ppt;*.pptx;*.odp;*.xls;*.xlsx;*.ods|All files|*.*";

    private readonly Func<ActionContext> _context;
    private readonly Action<string, string, string?> _notify;
    private readonly StudioDocument _doc = new();
    private readonly string _work = Paths.NewTempDir();
    private readonly HashSet<StudioPage> _selected = new();
    private readonly Stack<List<(StudioPage Page, int Rotation)>> _undo = new();
    private readonly Dictionary<StudioPage, Border> _tiles = new();

    private readonly WrapPanel _grid = new() { Margin = new Thickness(16) };
    private readonly ScrollViewer _gridScroll;
    private readonly PageEditor _editor;
    private readonly TextBlock _subtitle = Ui.Txt("", 12, color: Ui.Muted);
    private readonly TextBlock _status = Ui.Txt("", 12, color: Ui.Muted);
    private readonly TextBlock _flags = Ui.Txt("", 12, color: Ui.Accent);
    private readonly Border _empty;
    private readonly Border _busy;
    private readonly TextBlock _busyText = Ui.Txt("", 15, bold: true);
    private readonly Grid _gridView = new();
    private readonly List<Button> _needsPages = new();
    private readonly ContextMenu _moreMenu = new();
    private readonly MenuItem _searchableItem;

    private int _anchor = -1;
    private StudioPage? _pressPage;
    private Point _pressPoint;
    private bool _pendingSingleSelect;
    private bool _dragging;

    private List<StudioPage> Pages => _doc.Pages;

    public StudioWindow(IReadOnlyList<string> files, Func<ActionContext> context, Action<string, string, string?> notify)
    {
        _context = context;
        _notify = notify;
        Title = "PDF Studio · Droppo";
        Width = 1120;
        Height = 780;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowDrop = true;

        // ---- top bar ----
        var titles = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 12, 0) };
        var mark = Brand.Tile(32);
        mark.VerticalAlignment = VerticalAlignment.Center;
        titles.Children.Add(mark);
        var titleText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        titleText.Children.Add(Ui.Txt("PDF Studio", 16, bold: true));
        titleText.Children.Add(_subtitle);
        titles.Children.Add(titleText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        actions.Children.Add(Ui.Btn(Ui.Glyph.Add, "Add files", AddFiles, tooltip: "Add PDFs, images or Office files (or drop them here)"));
        var rotateLeft = Ui.Btn("", null, () => RotateSelected(-90), tooltip: "Rotate left");
        if (rotateLeft.Content is StackPanel iconRow && iconRow.Children.Count > 0)
            iconRow.Children[0].RenderTransform = new ScaleTransform(-1, 1, 7.5, 7.5); // mirrored "rotate right" icon = rotate left
        actions.Children.Add(NeedsPages(rotateLeft));
        actions.Children.Add(NeedsPages(Ui.Btn("", null, () => RotateSelected(90), tooltip: "Rotate right")));
        actions.Children.Add(NeedsPages(Ui.Btn(Ui.Glyph.Delete, null, DeleteSelected, tooltip: "Delete selected pages (Del)")));
        actions.Children.Add(NeedsPages(Ui.Btn(Ui.Glyph.Edit, "Edit page", EditSelected, tooltip: "Edit the selected page (or double-click a page)")));
        actions.Children.Add(NeedsPages(Ui.Btn(Ui.Glyph.Copy, "Extract", ExtractSelected, tooltip: "Save the selected pages as a new PDF")));
        var more = Ui.Btn(Ui.Glyph.More, "More", () => { }, tooltip: "Page numbers, watermark, password, OCR");
        more.Click += (_, _) => { _moreMenu.PlacementTarget = more; _moreMenu.Placement = PlacementMode.Bottom; _moreMenu.IsOpen = true; };
        actions.Children.Add(NeedsPages(more));
        actions.Children.Add(NeedsPages(Ui.Btn(Ui.Glyph.Save, "Save", () => _ = SaveAllAsync(null), primary: true, tooltip: "Save as a new PDF next to the original (Ctrl+S)")));

        var top = new DockPanel { Height = 60, Background = Ui.Surface, LastChildFill = false };
        DockPanel.SetDock(titles, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(titles);
        top.Children.Add(actions);
        var topBorder = new Border { Child = top, BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 1, 0, 1) };

        // ---- more menu ----
        _moreMenu.Items.Add(MenuItem("Page numbers...", EditPageNumbers));
        _moreMenu.Items.Add(MenuItem("Watermark...", EditWatermark));
        _moreMenu.Items.Add(MenuItem("Password...", EditPassword));
        _searchableItem = MenuItem("Make scanned pages searchable (OCR)", () =>
        {
            _doc.MakeSearchable = !_doc.MakeSearchable;
            UpdateChrome();
        });
        _searchableItem.IsCheckable = true;
        _moreMenu.Items.Add(_searchableItem);
        _moreMenu.Items.Add(new Separator());
        _moreMenu.Items.Add(MenuItem("Duplicate selected pages", DuplicateSelected));
        _moreMenu.Items.Add(MenuItem("Insert blank page after selection", () => _ = InsertBlankAsync()));
        _moreMenu.Items.Add(MenuItem("Select all  (Ctrl+A)", SelectAll));
        _moreMenu.Items.Add(new Separator());
        _moreMenu.Items.Add(MenuItem("Save as...", SaveAs));
        _moreMenu.Opened += (_, _) => _searchableItem.IsChecked = _doc.MakeSearchable;

        // ---- status bar ----
        var statusBar = new DockPanel { Height = 32, Background = Ui.Surface, LastChildFill = false };
        _status.Margin = new Thickness(18, 0, 0, 0);
        _flags.Margin = new Thickness(0, 0, 18, 0);
        DockPanel.SetDock(_status, Dock.Left);
        DockPanel.SetDock(_flags, Dock.Right);
        statusBar.Children.Add(_status);
        statusBar.Children.Add(_flags);
        var statusBorder = new Border { Child = statusBar, BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 1, 0, 0) };

        // ---- page grid ----
        _gridScroll = new ScrollViewer
        {
            Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, AllowDrop = true, Background = Ui.Background,
        };
        _gridScroll.PreviewMouseMove += Grid_MouseMove;
        _gridScroll.DragOver += Grid_DragOver;
        _gridScroll.DragLeave += (_, _) => RefreshSelectionVisuals();
        _gridScroll.Drop += Grid_Drop;
        _gridScroll.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == _gridScroll || e.OriginalSource == _grid) { _selected.Clear(); RefreshSelectionVisuals(); } };

        var emptyText = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var emptyMark = new Border
        {
            Width = 88, Height = 88, CornerRadius = new CornerRadius(24), Background = Ui.AccentSoft,
            HorizontalAlignment = HorizontalAlignment.Center, Child = Brand.Symbol(46, Ui.Accent, 2.1),
        };
        emptyText.Children.Add(emptyMark);
        var emptyLine = Ui.Txt("Drop PDFs, images or Office files here", 17, bold: true);
        emptyLine.HorizontalAlignment = HorizontalAlignment.Center;
        emptyLine.Margin = new Thickness(0, 18, 0, 4);
        emptyText.Children.Add(emptyLine);
        var emptySub = Ui.Txt("or click Add files. Then drag pages to put them in order.", 13, color: Ui.Muted);
        emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        emptyText.Children.Add(emptySub);
        _empty = new Border { Child = emptyText, IsHitTestVisible = false };

        _gridView.Children.Add(_gridScroll);
        _gridView.Children.Add(_empty);

        // ---- editor (hidden until a page is opened) ----
        _editor = new PageEditor(this, context, _work) { Visibility = Visibility.Collapsed };
        _editor.Closed += CloseEditor;
        _status.Text = ""; // the editor sets its own hint while being built; the pages view starts with its own

        // ---- busy overlay ----
        _busy = new Border
        {
            Background = Ui.Shade, Visibility = Visibility.Collapsed,
            Child = new Border
            {
                Background = Glass.CardBackground, BorderBrush = Glass.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(28, 18, 28, 18), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = _busyText,
            },
        };

        var center = new Grid();
        center.Children.Add(_gridView);
        center.Children.Add(_editor);
        center.Children.Add(_busy);

        var root = new DockPanel();
        DockPanel.SetDock(topBorder, Dock.Top);
        DockPanel.SetDock(statusBorder, Dock.Bottom);
        root.Children.Add(topBorder);
        root.Children.Add(statusBorder);
        root.Children.Add(center);
        Content = root;

        PreviewKeyDown += OnKey;
        Drop += (_, e) => { if (!e.Handled && TryGetFiles(e.Data) is { } dropped) { e.Handled = true; _ = LoadFilesAsync(dropped, Pages.Count); } };
        Closed += (_, _) => Paths.TryDelete(_work);

        UpdateChrome();
        if (files.Count > 0) Loaded += (_, _) => _ = LoadFilesAsync(files, 0);
    }

    // ---------- loading ----------

    private async Task LoadFilesAsync(IReadOnlyList<string> files, int insertAt)
    {
        SetBusy("Opening...");
        try
        {
            var ctx = _context();
            var progress = new Progress<string>(s => _busyText.Text = s);
            var loaded = await Task.Run(() => StudioLoader.LoadAsync(files, ctx, _work, progress));
            if (loaded.Count == 0)
            {
                SetStatus("Nothing to add: only PDFs, images and Office files can go in.");
                return;
            }
            PushUndo();
            Pages.InsertRange(Math.Clamp(insertAt, 0, Pages.Count), loaded);
            _selected.Clear();
            RebuildGrid();
            SetStatus($"Added {loaded.Count} page{(loaded.Count == 1 ? "" : "s")}. Drag to reorder, double-click to edit.");
        }
        catch (Exception ex)
        {
            Fail("Couldn't open that", ex);
        }
        finally
        {
            ClearBusy();
        }
    }

    private void AddFiles()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = FileFilter, Title = "Add files" };
        if (dialog.ShowDialog(this) == true)
            _ = LoadFilesAsync(dialog.FileNames, _selected.Count > 0 ? Pages.IndexOf(SelectedInOrder().Last()) + 1 : Pages.Count);
    }

    // ---------- grid ----------

    private void RebuildGrid()
    {
        _grid.Children.Clear();
        _tiles.Clear();
        var sources = Pages.Select(p => p.OriginalPath).Distinct().ToList();
        for (int i = 0; i < Pages.Count; i++)
        {
            var tile = MakeTile(Pages[i], i, sources.IndexOf(Pages[i].OriginalPath), sources.Count > 1);
            _tiles[Pages[i]] = tile;
            _grid.Children.Add(tile);
        }
        RefreshSelectionVisuals();
        UpdateChrome();
    }

    private static readonly Brush[] SourceColors =
    {
        Ui.Solid(0x0E, 0x9F, 0x8B), Ui.Solid(0x53, 0x4A, 0xB7), Ui.Solid(0xD8, 0x5A, 0x30),
        Ui.Solid(0x18, 0x5F, 0xA5), Ui.Solid(0xBA, 0x75, 0x17), Ui.Solid(0x99, 0x35, 0x56),
    };

    private Border MakeTile(StudioPage page, int index, int sourceIndex, bool showSource)
    {
        var image = new Image
        {
            Source = LoadBitmap(page.ThumbPath), Stretch = Stretch.Uniform, MaxWidth = 148, MaxHeight = 148,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            LayoutTransform = new RotateTransform(page.UserRotation),
        };
        var frame = new Grid { Width = 156, Height = 156 };
        frame.Children.Add(new Border
        {
            Child = image, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        if (page.Edits.Count > 0)
        {
            frame.Children.Add(new Border
            {
                Background = Ui.Accent, CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(0, 4, 4, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = "edited", Foreground = Brushes.White, FontSize = 10 },
            });
        }

        var label = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        var number = Ui.Txt((index + 1).ToString(), 13, bold: true);
        DockPanel.SetDock(number, Dock.Left);
        label.Children.Add(number);
        if (showSource)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            chip.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = SourceColors[Math.Max(0, sourceIndex) % SourceColors.Length], Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var name = Ui.Txt(page.SourceName, 11, color: Ui.Muted);
            name.MaxWidth = 110;
            chip.Children.Add(name);
            label.Children.Add(chip);
        }

        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(label);

        var tile = new Border
        {
            Width = 176, Margin = new Thickness(6), Padding = new Thickness(8), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(2), Child = stack, Tag = page, Cursor = Cursors.Hand,
            ToolTip = $"{page.SourceName}, page {page.SourceIndex + 1}",
        };
        tile.MouseEnter += (_, _) => { if (!_selected.Contains(page) && !_dragging) tile.Background = Ui.Hover; };
        tile.MouseLeave += (_, _) => { if (!_selected.Contains(page)) tile.Background = Ui.TileFill; };
        tile.MouseLeftButtonDown += (_, e) => Tile_Down(page, e);
        tile.MouseLeftButtonUp += (_, e) => Tile_Up(page);
        tile.MouseRightButtonUp += (_, e) =>
        {
            if (!_selected.Contains(page)) { _selected.Clear(); _selected.Add(page); _anchor = Pages.IndexOf(page); RefreshSelectionVisuals(); }
            TileMenu().IsOpen = true;
            e.Handled = true;
        };
        return tile;
    }

    private ContextMenu TileMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem("Edit page", EditSelected));
        menu.Items.Add(MenuItem("Rotate right", () => RotateSelected(90)));
        menu.Items.Add(MenuItem("Rotate left", () => RotateSelected(-90)));
        menu.Items.Add(MenuItem("Duplicate", DuplicateSelected));
        menu.Items.Add(MenuItem("Insert blank page after", () => _ = InsertBlankAsync()));
        menu.Items.Add(MenuItem("Extract to new PDF", ExtractSelected));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Delete", DeleteSelected));
        return menu;
    }

    private void Tile_Down(StudioPage page, MouseButtonEventArgs e)
    {
        e.Handled = true;
        int index = Pages.IndexOf(page);
        if (e.ClickCount == 2)
        {
            _pressPage = null;
            _selected.Clear();
            _selected.Add(page);
            OpenEditor(page);
            return;
        }

        _pressPage = page;
        _pressPoint = e.GetPosition(_grid);
        _pendingSingleSelect = false;
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0)
        {
            if (!_selected.Remove(page)) _selected.Add(page);
            _anchor = index;
        }
        else if ((mods & ModifierKeys.Shift) != 0 && _anchor >= 0 && _anchor < Pages.Count)
        {
            _selected.Clear();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) _selected.Add(Pages[i]);
        }
        else if (!_selected.Contains(page))
        {
            _selected.Clear();
            _selected.Add(page);
            _anchor = index;
        }
        else
        {
            _pendingSingleSelect = true; // might be the start of dragging several pages
        }
        RefreshSelectionVisuals();
    }

    private void Tile_Up(StudioPage page)
    {
        if (_pendingSingleSelect && !_dragging)
        {
            _selected.Clear();
            _selected.Add(page);
            _anchor = Pages.IndexOf(page);
            RefreshSelectionVisuals();
        }
        _pendingSingleSelect = false;
        _pressPage = null;
    }

    private void Grid_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressPage == null || _dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(_grid);
        if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6) return;

        _dragging = true;
        _pendingSingleSelect = false;
        try
        {
            DragDrop.DoDragDrop(_grid, new DataObject(InternalDragFormat, "pages"), DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            _pressPage = null;
            RefreshSelectionVisuals();
        }
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        bool internalDrag = e.Data.GetDataPresent(InternalDragFormat);
        if (!internalDrag && TryGetFiles(e.Data) == null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = internalDrag ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowDropMarker(InsertIndexAt(e.GetPosition(_grid)));

        // auto-scroll near the edges while dragging
        var y = e.GetPosition(_gridScroll).Y;
        if (y < 40) _gridScroll.ScrollToVerticalOffset(_gridScroll.VerticalOffset - 12);
        else if (y > _gridScroll.ActualHeight - 40) _gridScroll.ScrollToVerticalOffset(_gridScroll.VerticalOffset + 12);
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        int index = InsertIndexAt(e.GetPosition(_grid));
        if (e.Data.GetDataPresent(InternalDragFormat))
            MoveSelectedTo(index);
        else if (TryGetFiles(e.Data) is { } files)
            _ = LoadFilesAsync(files, index);
        RefreshSelectionVisuals();
    }

    /// <summary>Where a drop at this point would land: before the tile under the pointer, or after it.</summary>
    private int InsertIndexAt(Point p)
    {
        int best = Pages.Count;
        for (int i = 0; i < Pages.Count; i++)
        {
            if (!_tiles.TryGetValue(Pages[i], out var tile)) continue;
            var topLeft = tile.TranslatePoint(new Point(0, 0), _grid);
            double bottom = topLeft.Y + tile.ActualHeight;
            if (p.Y < topLeft.Y) return i;
            if (p.Y <= bottom)
            {
                if (p.X < topLeft.X + tile.ActualWidth / 2) return i;
                best = i + 1;
            }
        }
        return best;
    }

    private void ShowDropMarker(int index)
    {
        RefreshSelectionVisuals();
        if (Pages.Count == 0) return;
        if (index < Pages.Count && _tiles.TryGetValue(Pages[index], out var before))
        {
            before.BorderBrush = Ui.Accent;
            before.BorderThickness = new Thickness(5, 2, 2, 2);
        }
        else if (_tiles.TryGetValue(Pages[^1], out var last))
        {
            last.BorderBrush = Ui.Accent;
            last.BorderThickness = new Thickness(2, 2, 5, 2);
        }
    }

    private void RefreshSelectionVisuals()
    {
        foreach (var (page, tile) in _tiles)
        {
            bool on = _selected.Contains(page);
            tile.BorderBrush = on ? Ui.Accent : Glass.Enabled ? Ui.Edge : Brushes.Transparent;
            tile.Background = on ? Ui.AccentSoft : Ui.TileFill;
            tile.BorderThickness = new Thickness(2);
        }
        UpdateChrome();
    }

    // ---------- page operations ----------

    private List<StudioPage> SelectedInOrder() => Pages.Where(_selected.Contains).ToList();

    private void PushUndo()
    {
        _undo.Push(Pages.Select(p => (p, p.UserRotation)).ToList());
        if (_undo.Count > 50)
        {
            var keep = _undo.Take(50).Reverse().ToList();
            _undo.Clear();
            foreach (var s in keep) _undo.Push(s);
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) { SetStatus("Nothing to undo."); return; }
        var snapshot = _undo.Pop();
        Pages.Clear();
        foreach (var (page, rotation) in snapshot)
        {
            page.UserRotation = rotation;
            Pages.Add(page);
        }
        _selected.RemoveWhere(p => !Pages.Contains(p));
        RebuildGrid();
        SetStatus("Undone.");
    }

    private void MoveSelectedTo(int index)
    {
        var moving = SelectedInOrder();
        if (moving.Count == 0) return;
        int before = Pages.Take(index).Count(_selected.Contains);
        int target = index - before;
        PushUndo();
        Pages.RemoveAll(_selected.Contains);
        Pages.InsertRange(Math.Clamp(target, 0, Pages.Count), moving);
        RebuildGrid();
        SetStatus(moving.Count == 1 ? "Page moved." : $"{moving.Count} pages moved.");
    }

    private void RotateSelected(int degrees)
    {
        var targets = _selected.Count > 0 ? SelectedInOrder() : Pages.ToList();
        if (targets.Count == 0) return;
        PushUndo();
        foreach (var p in targets) p.Rotate(degrees);
        RebuildGrid();
        SetStatus(_selected.Count > 0 ? "Rotated." : "Rotated all pages (nothing was selected).");
    }

    private void DeleteSelected()
    {
        var targets = SelectedInOrder();
        if (targets.Count == 0) { SetStatus("Select pages first (click, Ctrl+click or Shift+click)."); return; }
        PushUndo();
        Pages.RemoveAll(_selected.Contains);
        _selected.Clear();
        RebuildGrid();
        SetStatus($"Deleted {targets.Count} page{(targets.Count == 1 ? "" : "s")}. Ctrl+Z to undo.");
    }

    private void DuplicateSelected()
    {
        var targets = SelectedInOrder();
        if (targets.Count == 0) { SetStatus("Select pages to duplicate first."); return; }
        PushUndo();
        int insertAt = Pages.IndexOf(targets[^1]) + 1;
        var copies = targets.Select(p => p.CloneShallowEdits()).ToList();
        Pages.InsertRange(insertAt, copies);
        _selected.Clear();
        foreach (var c in copies) _selected.Add(c);
        RebuildGrid();
    }

    private async Task InsertBlankAsync()
    {
        var reference = _selected.Count > 0 ? SelectedInOrder()[^1] : Pages.LastOrDefault();
        double w = reference?.DisplayWidth ?? 595.28, h = reference?.DisplayHeight ?? 841.89;
        try
        {
            var ctx = _context();
            var pdf = StudioTools.CreateBlankPdf(_work, w, h);
            var thumb = Path.Combine(_work, Path.GetFileNameWithoutExtension(pdf) + ".png");
            await Task.Run(() => ctx.Renderer.RenderPageAsync(pdf, 0, StudioLoader.ThumbDpi, thumb, ctx.Token));
            var blank = new StudioPage { SourcePdf = pdf, SourceIndex = 0, WidthPt = w, HeightPt = h, SourceName = "Blank page", ThumbPath = thumb };
            PushUndo();
            Pages.Insert(reference == null ? Pages.Count : Pages.IndexOf(reference) + 1, blank);
            _selected.Clear();
            _selected.Add(blank);
            RebuildGrid();
        }
        catch (Exception ex)
        {
            Fail("Couldn't add a blank page", ex);
        }
    }

    private void SelectAll()
    {
        _selected.Clear();
        foreach (var p in Pages) _selected.Add(p);
        RefreshSelectionVisuals();
    }

    // ---------- editor ----------

    private void EditSelected()
    {
        var target = SelectedInOrder().FirstOrDefault() ?? Pages.FirstOrDefault();
        if (target != null) OpenEditor(target);
    }

    private void OpenEditor(StudioPage page)
    {
        _gridView.Visibility = Visibility.Collapsed;
        _editor.Visibility = Visibility.Visible;
        _ = _editor.OpenAsync(Pages, Pages.IndexOf(page));
        UpdateChrome();
    }

    private void CloseEditor()
    {
        _editor.Visibility = Visibility.Collapsed;
        _gridView.Visibility = Visibility.Visible;
        RebuildGrid(); // refresh "edited" badges
        Focus();
    }

    // ---------- document settings ----------

    private void EditPageNumbers()
    {
        var (opts, removed) = Dialogs.PageNumbers(_doc.PageNumbers, this);
        if (removed) _doc.PageNumbers = null;
        else if (opts != null) _doc.PageNumbers = opts;
        UpdateChrome();
    }

    private void EditWatermark()
    {
        var (opts, removed) = Dialogs.Watermark(_doc.Watermark, this);
        if (removed) _doc.Watermark = null;
        else if (opts != null) _doc.Watermark = opts;
        UpdateChrome();
    }

    private void EditPassword()
    {
        if (_doc.Password != null)
        {
            var answer = MessageBox.Show(this, "The saved PDF will need a password to open.\n\nRemove the password?", "Password", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) _doc.Password = null;
        }
        else
        {
            var password = Dialogs.AskPassword("Protect with a password", "Anyone opening the saved PDF will need this password. Don't lose it, it can't be recovered.", confirm: true, this);
            if (!string.IsNullOrEmpty(password)) _doc.Password = password;
        }
        UpdateChrome();
    }

    // ---------- saving ----------

    private string DefaultTarget(IReadOnlyList<StudioPage> pages, string suffix)
    {
        var origin = pages.Select(p => p.OriginalPath).FirstOrDefault(p => !string.IsNullOrEmpty(p))
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Studio.pdf");
        bool manySources = pages.Select(p => p.OriginalPath).Distinct().Count() > 1;
        var settings = Settings.Load();
        if (settings.OutputMode == "Folder" && !string.IsNullOrWhiteSpace(settings.OutputFolder))
        {
            // Settings: everything goes to one folder
            Directory.CreateDirectory(settings.OutputFolder);
            var stem = Path.GetFileNameWithoutExtension(origin) + (manySources && suffix == "-edited" ? "-combined" : suffix);
            return Paths.Unique(settings.OutputFolder, stem, ".pdf");
        }
        return Paths.NextTo(origin, manySources && suffix == "-edited" ? "-combined" : suffix, ".pdf");
    }

    private static bool AskWhereToSave => Settings.Load().OutputMode == "Ask";

    private Task SaveAllAsync(string? path)
    {
        if (path == null && AskWhereToSave) { SaveAs(Pages.ToList(), "-edited"); return Task.CompletedTask; }
        return SaveAsync(Pages.ToList(), path ?? DefaultTarget(Pages, "-edited"));
    }

    private void ExtractSelected()
    {
        var pages = SelectedInOrder();
        if (pages.Count == 0) { SetStatus("Select the pages to extract first."); return; }
        if (AskWhereToSave) { SaveAs(pages, "-extract"); return; }
        _ = SaveAsync(pages, DefaultTarget(pages, "-extract"));
    }

    private void SaveAs() => SaveAs(Pages.ToList(), "-edited");

    private void SaveAs(List<StudioPage> pages, string suffix)
    {
        if (pages.Count == 0) return;
        var suggested = DefaultTarget(pages, suffix);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PDF|*.pdf", FileName = Path.GetFileName(suggested), InitialDirectory = Path.GetDirectoryName(suggested) ?? "",
            Title = "Save PDF as", OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var p in Pages)
        {
            if (string.Equals(Path.GetFullPath(p.SourcePdf), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "Pick a different name: this file is one of the pages' sources.", "Save as", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
        }
        _ = SaveAsync(pages, dialog.FileName);
    }

    private async Task SaveAsync(List<StudioPage> pages, string target)
    {
        if (pages.Count == 0) { SetStatus("There are no pages to save."); return; }
        _editor.CommitPending();
        SetBusy("Saving...");
        try
        {
            var ctx = _context();
            var progress = new Progress<string>(s => _busyText.Text = s);
            await Task.Run(() => StudioExporter.ExportAsync(_doc, pages, target, ctx, progress));
            var name = Path.GetFileName(target);
            SetStatus($"Saved {name}");
            _notify("All done.", "Saved " + name, target);
        }
        catch (Exception ex)
        {
            Fail("Couldn't save", ex);
        }
        finally
        {
            ClearBusy();
        }
    }

    // ---------- keyboard ----------

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_busy.Visibility == Visibility.Visible) { e.Handled = true; return; }
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (ctrl && e.Key == Key.S) { e.Handled = true; _ = SaveAllAsync(null); return; }

        if (_editor.Visibility == Visibility.Visible)
        {
            _editor.HandleKey(e);
            return;
        }

        if (e.Key == Key.Delete || e.Key == Key.Back) { e.Handled = true; DeleteSelected(); }
        else if (ctrl && e.Key == Key.Z) { e.Handled = true; Undo(); }
        else if (ctrl && e.Key == Key.A) { e.Handled = true; SelectAll(); }
        else if (ctrl && e.Key == Key.R) { e.Handled = true; RotateSelected(90); }
        else if (e.Key == Key.Enter && _selected.Count > 0) { e.Handled = true; EditSelected(); }
    }

    // ---------- chrome ----------

    private void UpdateChrome()
    {
        int sources = Pages.Select(p => p.OriginalPath).Distinct().Count();
        _subtitle.Text = Pages.Count == 0 ? "No pages yet"
            : $"{Pages.Count} page{(Pages.Count == 1 ? "" : "s")}" + (sources > 1 ? $" from {sources} files" : $" · {Pages[0].SourceName}");
        _empty.Visibility = Pages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var b in _needsPages) b.IsEnabled = Pages.Count > 0;

        var flags = new List<string>();
        if (_doc.PageNumbers != null) flags.Add("Page numbers");
        if (_doc.Watermark != null) flags.Add("Watermark");
        if (_doc.Password != null) flags.Add("Password");
        if (_doc.MakeSearchable) flags.Add("OCR on save");
        _flags.Text = flags.Count == 0 ? "" : "On save: " + string.Join(" · ", flags);

        if (_editor?.Visibility != Visibility.Visible && string.IsNullOrEmpty(_status.Text))
            _status.Text = "Drag pages to reorder · Ctrl/Shift+click to select several · Double-click to edit";
        if (_selected.Count > 1) _status.Text = $"{_selected.Count} pages selected";
    }

    internal void SetStatus(string text) => _status.Text = text;

    internal void SetBusy(string text)
    {
        _busyText.Text = text;
        if (_busy.Visibility != Visibility.Visible)
        {
            _busy.Visibility = Visibility.Visible;
            Motion.FadeIn(_busy, 120);
        }
    }

    internal void ClearBusy() => _busy.Visibility = Visibility.Collapsed;

    internal void Fail(string title, Exception ex)
    {
        Log.Write($"Studio: {title}: {ex}");
        MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private Button NeedsPages(Button b)
    {
        _needsPages.Add(b);
        return b;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    internal static IReadOnlyList<string>? TryGetFiles(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                return files;
        }
        catch
        {
            // some drag sources hand over data we can't read
        }
        return null;
    }

    internal static BitmapImage? LoadBitmap(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // read now, don't keep the file locked
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't load image " + path + ": " + ex.Message);
            return null;
        }
    }
}
