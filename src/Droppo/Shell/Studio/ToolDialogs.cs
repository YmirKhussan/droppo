using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Droppo.Core;
using Droppo.Core.Studio;

namespace Droppo.Shell.Studio;

/// <summary>Dialogs for the wheel's "pick options first" actions: trim, order, resize, rename, signature.</summary>
internal static partial class Dialogs
{
    // ---------- trim ----------

    public static TrimRange? AskTrim(string file, double duration, bool hasVideo, Func<double, Task<string?>> frameAt, Window? owner)
    {
        var w = Ui.Dialog("Trim " + System.IO.Path.GetFileName(file), owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = hasVideo ? 560 : 420 };
        panel.Children.Add(Ui.Txt("Choose the part to keep.", 13));

        var startImage = new Image { Height = 150, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
        var endImage = new Image { Height = 150, Stretch = Stretch.Uniform };
        if (hasVideo)
        {
            var frames = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            frames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            frames.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new Border { Background = Brushes.Black, Child = startImage, Margin = new Thickness(0, 0, 4, 0), Height = 150 };
            var right = new Border { Background = Brushes.Black, Child = endImage, Margin = new Thickness(4, 0, 0, 0), Height = 150 };
            Grid.SetColumn(right, 1);
            frames.Children.Add(left);
            frames.Children.Add(right);
            panel.Children.Add(frames);
        }

        var startSlider = new Slider { Minimum = 0, Maximum = duration, Value = 0, Margin = new Thickness(0, 4, 0, 0) };
        var endSlider = new Slider { Minimum = 0, Maximum = duration, Value = duration, Margin = new Thickness(0, 4, 0, 0) };
        var startBox = new TextBox { Width = 80, Padding = new Thickness(4), Text = MediaEditOps.FormatTime(0) };
        var endBox = new TextBox { Width = 80, Padding = new Thickness(4), Text = MediaEditOps.FormatTime(duration) };
        var summary = new TextBlock { Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

        StackPanel Row(string label, TextBox box, Slider slider)
        {
            var row = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var head = new DockPanel();
            var l = Ui.Txt(label, 12, color: Ui.Muted);
            DockPanel.SetDock(l, Dock.Left);
            head.Children.Add(l);
            box.HorizontalAlignment = HorizontalAlignment.Right;
            head.Children.Add(box);
            row.Children.Add(head);
            row.Children.Add(slider);
            return row;
        }
        panel.Children.Add(Row("Start", startBox, startSlider));
        panel.Children.Add(Row("End", endBox, endSlider));
        panel.Children.Add(summary);
        panel.Children.Add(error);

        bool syncing = false;
        var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        int previewVersion = 0;
        previewTimer.Tick += async (_, _) =>
        {
            previewTimer.Stop();
            if (!hasVideo) return;
            int version = ++previewVersion;
            var s = await frameAt(startSlider.Value);
            var e = await frameAt(Math.Max(0, endSlider.Value - 0.05));
            if (version != previewVersion) return;
            startImage.Source = StudioWindow.LoadBitmap(s);
            endImage.Source = StudioWindow.LoadBitmap(e);
        };

        void Update(bool fromSlider)
        {
            if (syncing) return;
            syncing = true;
            if (endSlider.Value - startSlider.Value < 0.1)
            {
                if (fromSlider && startSlider.IsMouseCaptureWithin) endSlider.Value = Math.Min(duration, startSlider.Value + 0.1);
                else startSlider.Value = Math.Max(0, endSlider.Value - 0.1);
            }
            if (fromSlider)
            {
                startBox.Text = MediaEditOps.FormatTime(startSlider.Value);
                endBox.Text = MediaEditOps.FormatTime(endSlider.Value);
            }
            summary.Text = $"Keeping {MediaEditOps.FormatTime(endSlider.Value - startSlider.Value)} of {MediaEditOps.FormatTime(duration)}";
            error.Visibility = Visibility.Collapsed;
            syncing = false;
            previewTimer.Stop();
            previewTimer.Start();
        }
        startSlider.ValueChanged += (_, _) => Update(true);
        endSlider.ValueChanged += (_, _) => Update(true);

        void FromBox(TextBox box, Slider slider)
        {
            if (TryParseTime(box.Text, out var t)) { slider.Value = Math.Clamp(t, 0, duration); }
            else box.Text = MediaEditOps.FormatTime(slider.Value);
        }
        startBox.LostKeyboardFocus += (_, _) => FromBox(startBox, startSlider);
        endBox.LostKeyboardFocus += (_, _) => FromBox(endBox, endSlider);

        TrimRange? result = null;
        void Accept()
        {
            FromBox(startBox, startSlider);
            FromBox(endBox, endSlider);
            if (endSlider.Value - startSlider.Value < 0.1) { Show(error, "The part to keep is too short."); return; }
            if (startSlider.Value <= 0.01 && endSlider.Value >= duration - 0.01) { Show(error, "Move Start or End first, otherwise nothing changes."); return; }
            result = new TrimRange(startSlider.Value, endSlider.Value);
            w.DialogResult = true;
        }
        panel.Children.Add(Ui.ButtonRow(Ui.Btn(null, "Cancel", () => w.DialogResult = false), Ui.Btn(null, "Trim", Accept, primary: true)));
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        w.Content = panel;
        w.Loaded += (_, _) => { w.Activate(); Update(true); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Accepts 75, 1:15, 1:15.5 or 0:01:15.</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }

    // ---------- order ----------

    public static IReadOnlyList<string>? AskOrder(string title, string message, IReadOnlyList<string> files, Window? owner)
    {
        var w = Ui.Dialog(title, owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 440 };
        var msg = Ui.Txt(message, 13);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.TextTrimming = TextTrimming.None;
        panel.Children.Add(msg);

        var order = files.ToList();
        var list = new ListBox { Height = Math.Min(320, 40 + order.Count * 28), Margin = new Thickness(0, 12, 0, 0), FontSize = 13 };
        void Refresh(int select)
        {
            list.Items.Clear();
            for (int i = 0; i < order.Count; i++) list.Items.Add($"{i + 1}.  {System.IO.Path.GetFileName(order[i])}");
            list.SelectedIndex = Math.Clamp(select, -1, order.Count - 1);
        }
        void Move(int delta)
        {
            int i = list.SelectedIndex, j = i + delta;
            if (i < 0 || j < 0 || j >= order.Count) return;
            (order[i], order[j]) = (order[j], order[i]);
            Refresh(j);
        }
        Refresh(0);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Ui.Btn("", "Up", () => Move(-1), tooltip: "Move up (Alt+Up)"));
        buttons.Children.Add(Ui.Btn("", "Down", () => Move(1), tooltip: "Move down (Alt+Down)"));
        buttons.Children.Add(Ui.Btn(null, "Reverse", () => { order.Reverse(); Refresh(list.SelectedIndex); }));
        panel.Children.Add(list);
        panel.Children.Add(buttons);

        IReadOnlyList<string>? result = null;
        panel.Children.Add(Ui.ButtonRow(Ui.Btn(null, "Cancel", () => w.DialogResult = false),
            Ui.Btn(null, "Continue", () => { result = order.ToList(); w.DialogResult = true; }, primary: true)));
        w.PreviewKeyDown += (_, e) =>
        {
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == 0) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Up) { e.Handled = true; Move(-1); }
            else if (key == Key.Down) { e.Handled = true; Move(1); }
        };
        w.Content = panel;
        w.Loaded += (_, _) => { w.Activate(); list.Focus(); };
        return w.ShowDialog() == true ? result : null;
    }

    // ---------- resize ----------

    private sealed record Preset(string Label, int W, int H, FitMode Mode, double? Percent = null, bool Icon = false);

    public static ResizeSpec? AskResize(int fileCount, (int W, int H)? firstSize, Window? owner)
    {
        var w = Ui.Dialog("Resize", owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 420 };
        panel.Children.Add(Ui.Txt(firstSize is { } fs
            ? (fileCount > 1 ? $"{fileCount} images. The first is {fs.W} × {fs.H} px." : $"Now {fs.W} × {fs.H} px.")
            : $"{fileCount} image(s).", 13));

        var presets = new[]
        {
            new Preset("Passport photo (35×50 mm)", 413, 531, FitMode.Crop),
            new Preset("Square 600 × 600 (forms, profile)", 600, 600, FitMode.Crop),
            new Preset("Instagram post 1080 × 1080", 1080, 1080, FitMode.Crop),
            new Preset("Instagram portrait 1080 × 1350", 1080, 1350, FitMode.Crop),
            new Preset("Full HD 1920 × 1080", 1920, 1080, FitMode.Fit),
            new Preset("Half size (50%)", 0, 0, FitMode.Fit, 50),
            new Preset("Quarter size (25%)", 0, 0, FitMode.Fit, 25),
            new Preset("App icon (.ico, 256 × 256)", 256, 256, FitMode.Fit, null, true),
        };
        var radios = new List<RadioButton>();
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var p in presets)
        {
            var r = new RadioButton { Content = p.Label, GroupName = "preset", Margin = new Thickness(0, 4, 0, 4) };
            radios.Add(r);
            list.Children.Add(r);
        }
        var customRadio = new RadioButton { Content = "Custom size", GroupName = "preset", Margin = new Thickness(0, 4, 0, 4) };
        list.Children.Add(customRadio);
        panel.Children.Add(list);

        var widthBox = new TextBox { Width = 70, Padding = new Thickness(4), Text = firstSize?.W.ToString() ?? "1000" };
        var heightBox = new TextBox { Width = 70, Padding = new Thickness(4), Text = firstSize?.H.ToString() ?? "1000" };
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 2, 0, 0) };
        customRow.Children.Add(widthBox);
        customRow.Children.Add(Ui.Txt("  ×  ", 13));
        customRow.Children.Add(heightBox);
        customRow.Children.Add(Ui.Txt("  px", 13, color: Ui.Muted));
        panel.Children.Add(customRow);

        panel.Children.Add(new TextBlock { Text = "If the shape is different", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) });
        var crop = new RadioButton { Content = "Crop to fill (cuts the edges, centred)", GroupName = "mode", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
        var fit = new RadioButton { Content = "Fit inside (adds white or see-through bars)", GroupName = "mode", Margin = new Thickness(0, 2, 0, 2) };
        panel.Children.Add(crop);
        panel.Children.Add(fit);
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        void Sync()
        {
            int i = radios.FindIndex(r => r.IsChecked == true);
            bool custom = customRadio.IsChecked == true;
            customRow.IsEnabled = custom;
            bool fixedSize = custom || (i >= 0 && presets[i].Percent == null && !presets[i].Icon);
            crop.IsEnabled = fit.IsEnabled = fixedSize;
            if (i >= 0 && !custom) { crop.IsChecked = presets[i].Mode == FitMode.Crop; fit.IsChecked = presets[i].Mode == FitMode.Fit; }
            error.Visibility = Visibility.Collapsed;
        }
        foreach (var r in radios) r.Checked += (_, _) => Sync();
        customRadio.Checked += (_, _) => Sync();
        radios[1].IsChecked = true;
        Sync();

        ResizeSpec? result = null;
        void Accept()
        {
            var mode = fit.IsChecked == true ? FitMode.Fit : FitMode.Crop;
            if (customRadio.IsChecked == true)
            {
                bool okW = int.TryParse(widthBox.Text.Trim(), out var cw), okH = int.TryParse(heightBox.Text.Trim(), out var ch);
                if (!okW || !okH || cw < 8 || ch < 8 || cw > 20000 || ch > 20000) { Show(error, "Enter a width and height between 8 and 20000 pixels."); return; }
                result = new ResizeSpec(cw, ch, mode);
            }
            else
            {
                int i = radios.FindIndex(r => r.IsChecked == true);
                if (i < 0) { Show(error, "Pick a size."); return; }
                var p = presets[i];
                result = new ResizeSpec(p.W, p.H, p.Percent != null || p.Icon ? p.Mode : mode, p.Percent, p.Icon);
            }
            w.DialogResult = true;
        }
        panel.Children.Add(Ui.ButtonRow(Ui.Btn(null, "Cancel", () => w.DialogResult = false), Ui.Btn(null, "Resize", Accept, primary: true)));
        w.Content = panel;
        w.Loaded += (_, _) => w.Activate();
        return w.ShowDialog() == true ? result : null;
    }

    // ---------- rename ----------

    public static RenameRequest? AskRename(IReadOnlyList<string> files, Window? owner)
    {
        var w = Ui.Dialog("Rename " + files.Count + " items", owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 480 };
        panel.Children.Add(Ui.Txt("New name", 12, color: Ui.Muted));
        var pattern = new TextBox { Text = "{name}", Padding = new Thickness(6), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(pattern);
        var help = Ui.Txt("# = number (### gives 001)   {name} = current name   {date} = file date", 11, color: Ui.Muted);
        help.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(help);

        var quick = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var example in new[] { "Photo ###", "{date} {name}", "{name} (edited)", "Invoice ###" })
        {
            var b = Ui.Btn(null, example, () => { pattern.Text = example; pattern.Focus(); pattern.CaretIndex = pattern.Text.Length; });
            b.Margin = new Thickness(0, 0, 6, 6);
            quick.Children.Add(b);
        }
        panel.Children.Add(quick);

        var startRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        startRow.Children.Add(Ui.Txt("Start numbering at ", 12, color: Ui.Muted));
        var start = new TextBox { Text = "1", Width = 60, Padding = new Thickness(4) };
        startRow.Children.Add(start);
        panel.Children.Add(startRow);

        panel.Children.Add(new TextBlock { Text = "Preview", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 4) });
        var preview = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
        panel.Children.Add(new Border { Child = preview, Background = Ui.Background, Padding = new Thickness(10), CornerRadius = new CornerRadius(6), MinHeight = 60 });
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(error);
        var go = Ui.Btn(null, "Rename", () => { }, primary: true);

        RenameRequest? Current(out string? problem)
        {
            problem = null;
            if (!int.TryParse(start.Text.Trim(), out var n) || n < 0) { problem = "Start number must be a whole number."; return null; }
            var request = new RenameRequest(pattern.Text, n);
            try
            {
                var plan = RenameOps.Plan(files, request);
                var shown = plan.Take(8).Select(m => $"{Short(System.IO.Path.GetFileName(m.From))}  →  {System.IO.Path.GetFileName(m.To)}").ToList();
                if (plan.Count > 8) shown.Add($"... and {plan.Count - 8} more");
                preview.Text = string.Join("\n", shown);
                return request;
            }
            catch (ArgumentException ex)
            {
                problem = ex.Message;
                return null;
            }
        }
        static string Short(string s) => s.Length > 26 ? s[..23] + "..." : s;

        void Refresh()
        {
            var ok = Current(out var problem) != null;
            go.IsEnabled = ok;
            if (problem != null) Show(error, problem); else error.Visibility = Visibility.Collapsed;
        }
        pattern.TextChanged += (_, _) => Refresh();
        start.TextChanged += (_, _) => Refresh();
        Refresh();

        RenameRequest? result = null;
        go.Click += (_, _) =>
        {
            result = Current(out var problem);
            if (result == null) { Show(error, problem ?? "Check the name."); return; }
            w.DialogResult = true;
        };
        panel.Children.Add(Ui.ButtonRow(Ui.Btn(null, "Cancel", () => w.DialogResult = false), go));
        w.Content = panel;
        w.Loaded += (_, _) => { w.Activate(); pattern.Focus(); pattern.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    // ---------- signature pad ----------

    /// <summary>Draw your signature once; it's saved for every PDF after that.</summary>
    public static SavedSignature? DrawSignature(Window? owner)
    {
        var w = Ui.Dialog("Your signature", owner);
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Ui.Txt("Sign with your mouse or pen. It's saved on this PC so you can stamp it on any PDF.", 13));

        var strokes = new List<List<(double X, double Y)>>();
        var pad = new Canvas { Width = 520, Height = 200, Background = Brushes.White, ClipToBounds = true, Cursor = Cursors.Pen };
        var line = new Line { X1 = 30, X2 = 490, Y1 = 160, Y2 = 160, Stroke = Ui.Line, StrokeThickness = 1, IsHitTestVisible = false };
        pad.Children.Add(line);
        Polyline? live = null;
        pad.MouseLeftButtonDown += (_, e) =>
        {
            var p = e.GetPosition(pad);
            var stroke = new List<(double, double)> { (p.X, p.Y) };
            strokes.Add(stroke);
            live = new Polyline { Stroke = Ui.Solid(Rgb.Blue), StrokeThickness = 2.4, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            live.Points.Add(p);
            pad.Children.Add(live);
            pad.CaptureMouse();
        };
        pad.MouseMove += (_, e) =>
        {
            if (live == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(pad);
            strokes[^1].Add((p.X, p.Y));
            live.Points.Add(p);
        };
        pad.MouseLeftButtonUp += (_, _) => { live = null; pad.ReleaseMouseCapture(); };

        panel.Children.Add(new Border { Child = pad, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 12, 0, 0) });
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        SavedSignature? result = null;
        var clear = Ui.Btn(null, "Clear", () =>
        {
            strokes.Clear();
            pad.Children.Clear();
            pad.Children.Add(line);
        });
        var save = Ui.Btn(null, "Save signature", () =>
        {
            if (strokes.Sum(s => s.Count) < 4) { Show(error, "Draw your signature first."); return; }
            result = SavedSignature.FromStrokes(strokes.Select(s => (IReadOnlyList<(double, double)>)s).ToList());
            result.Save();
            w.DialogResult = true;
        }, primary: true);
        panel.Children.Add(Ui.ButtonRow(clear, Ui.Btn(null, "Cancel", () => w.DialogResult = false), save));
        w.Content = panel;
        w.Loaded += (_, _) => w.Activate();
        return w.ShowDialog() == true ? result : null;
    }
}
