using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Droppo.Core;
using Droppo.Core.Studio;

namespace Droppo.Shell.Studio;

/// <summary>Small, focused dialogs. Each returns null when cancelled.</summary>
internal static partial class Dialogs
{
    /// <summary>Asks for a password. With confirm, asks twice and checks they match.</summary>
    public static string? AskPassword(string title, string message, bool confirm, Window? owner)
    {
        var w = Ui.Dialog(title, owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 340 };
        var msg = Ui.Txt(message, 13);
        msg.TextWrapping = TextWrapping.Wrap;
        msg.TextTrimming = TextTrimming.None;
        panel.Children.Add(msg);

        var first = new PasswordBox { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(6), FontSize = 14 };
        panel.Children.Add(first);
        PasswordBox? second = null;
        if (confirm)
        {
            panel.Children.Add(new TextBlock { Text = "Type it again:", Margin = new Thickness(0, 10, 0, 0), Foreground = Ui.Muted, FontSize = 12 });
            second = new PasswordBox { Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6), FontSize = 14 };
            panel.Children.Add(second);
        }
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        string? result = null;
        void Accept()
        {
            if (first.Password.Length == 0) { Show(error, "Enter a password first."); return; }
            if (second != null && second.Password != first.Password) { Show(error, "The two passwords don't match."); return; }
            result = first.Password;
            w.DialogResult = true;
        }

        var ok = Ui.Btn(null, "OK", Accept, primary: true);
        var cancel = Ui.Btn(null, "Cancel", () => w.DialogResult = false);
        panel.Children.Add(Ui.ButtonRow(cancel, ok));
        first.PasswordChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        w.Content = panel;
        w.Loaded += (_, _) => { w.Activate(); first.Focus(); };
        return w.ShowDialog() == true ? result : null;
    }

    /// <summary>Page numbers setup. Returns (options, removed). Options null + removed false = cancelled.</summary>
    public static (PageNumberOptions? Options, bool Removed) PageNumbers(PageNumberOptions? current, Window owner)
    {
        var w = Ui.Dialog("Page numbers", owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 320 };
        var opts = current ?? new PageNumberOptions();

        panel.Children.Add(Ui.Txt("Position", 12, color: Ui.Muted));
        var positions = new[] { ("Bottom centre", NumberPosition.BottomCenter), ("Bottom right", NumberPosition.BottomRight), ("Top right", NumberPosition.TopRight) };
        var posButtons = new RadioButton[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            posButtons[i] = new RadioButton { Content = positions[i].Item1, GroupName = "pos", Margin = new Thickness(0, 6, 0, 0), IsChecked = opts.Position == positions[i].Item2 };
            panel.Children.Add(posButtons[i]);
        }

        panel.Children.Add(new TextBlock { Text = "Style", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) });
        var formats = new[] { ("1", "{n}"), ("Page 1", "Page {n}"), ("Page 1 of 10", "Page {n} of {total}"), ("1 / 10", "{n} / {total}") };
        var formatBox = new ComboBox { Margin = new Thickness(0, 4, 0, 0) };
        int selected = 0;
        for (int i = 0; i < formats.Length; i++)
        {
            formatBox.Items.Add(formats[i].Item1);
            if (formats[i].Item2 == opts.Format) selected = i;
        }
        formatBox.SelectedIndex = selected;
        panel.Children.Add(formatBox);

        panel.Children.Add(new TextBlock { Text = "Start counting at", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) });
        var start = new TextBox { Text = opts.StartAt.ToString(), Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(4), Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(start);
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        PageNumberOptions? result = null;
        bool removed = false;
        var apply = Ui.Btn(null, "Apply", () =>
        {
            if (!int.TryParse(start.Text.Trim(), out var n) || n < 0 || n > 99999) { Show(error, "Enter a whole number, like 1."); return; }
            int pos = Array.FindIndex(posButtons, b => b.IsChecked == true);
            result = new PageNumberOptions
            {
                Position = positions[Math.Max(0, pos)].Item2,
                Format = formats[Math.Max(0, formatBox.SelectedIndex)].Item2,
                StartAt = n,
            };
            w.DialogResult = true;
        }, primary: true);
        var remove = Ui.Btn(null, "Remove", () => { removed = true; w.DialogResult = true; });
        remove.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        var cancel = Ui.Btn(null, "Cancel", () => w.DialogResult = false);
        panel.Children.Add(Ui.ButtonRow(remove, cancel, apply));
        start.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        w.Content = panel;
        return w.ShowDialog() == true ? (result, removed) : (null, false);
    }

    /// <summary>Watermark setup. Same return convention as <see cref="PageNumbers"/>.</summary>
    public static (WatermarkOptions? Options, bool Removed) Watermark(WatermarkOptions? current, Window owner)
    {
        var w = Ui.Dialog("Watermark", owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 320 };
        var opts = current ?? new WatermarkOptions();

        panel.Children.Add(Ui.Txt("Text", 12, color: Ui.Muted));
        var text = new TextBox { Text = opts.Text, Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(6), FontSize = 14 };
        panel.Children.Add(text);

        panel.Children.Add(new TextBlock { Text = "Colour", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) });
        var colours = new[] { ("Grey", Rgb.Gray), ("Red", Rgb.Red), ("Blue", Rgb.Blue) };
        var colourButtons = new RadioButton[colours.Length];
        var colourRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        for (int i = 0; i < colours.Length; i++)
        {
            colourButtons[i] = new RadioButton { Content = colours[i].Item1, GroupName = "colour", Margin = new Thickness(0, 0, 16, 0), IsChecked = opts.Color == colours[i].Item2 };
            colourRow.Children.Add(colourButtons[i]);
        }
        if (Array.TrueForAll(colourButtons, b => b.IsChecked != true)) colourButtons[0].IsChecked = true;
        panel.Children.Add(colourRow);

        var strengthLabel = new TextBlock { Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 14, 0, 0) };
        var strength = new Slider { Minimum = 5, Maximum = 60, Value = Math.Round(opts.Opacity * 100), Margin = new Thickness(0, 4, 0, 0), IsSnapToTickEnabled = true, TickFrequency = 1 };
        void UpdateStrength() => strengthLabel.Text = $"Strength: {strength.Value:0}%";
        strength.ValueChanged += (_, _) => UpdateStrength();
        UpdateStrength();
        panel.Children.Add(strengthLabel);
        panel.Children.Add(strength);

        var diagonal = new CheckBox { Content = "Diagonal", IsChecked = opts.Diagonal, Margin = new Thickness(0, 12, 0, 0) };
        panel.Children.Add(diagonal);
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        panel.Children.Add(error);

        WatermarkOptions? result = null;
        bool removed = false;
        var apply = Ui.Btn(null, "Apply", () =>
        {
            if (string.IsNullOrWhiteSpace(text.Text)) { Show(error, "Type the watermark text first."); return; }
            int c = Array.FindIndex(colourButtons, b => b.IsChecked == true);
            result = new WatermarkOptions
            {
                Text = text.Text.Trim(),
                Opacity = strength.Value / 100.0,
                Diagonal = diagonal.IsChecked == true,
                Color = colours[Math.Max(0, c)].Item2,
            };
            w.DialogResult = true;
        }, primary: true);
        var remove = Ui.Btn(null, "Remove", () => { removed = true; w.DialogResult = true; });
        remove.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
        var cancel = Ui.Btn(null, "Cancel", () => w.DialogResult = false);
        panel.Children.Add(Ui.ButtonRow(remove, cancel, apply));
        text.TextChanged += (_, _) => error.Visibility = Visibility.Collapsed;
        w.Content = panel;
        w.Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
        return w.ShowDialog() == true ? (result, removed) : (null, false);
    }

    /// <summary>"Make it smaller": pick a size in MB or a % of the original, with quick presets.</summary>
    public static SizeTarget? AskTargetSize(long largestBytes, int fileCount, SizeTarget last, Window? owner)
    {
        var w = Ui.Dialog("Make it smaller", owner);
        var panel = new StackPanel { Margin = new Thickness(22), Width = 380 };

        var intro = Ui.Txt(fileCount == 1
            ? $"Original size: {Paths.HumanSize(largestBytes)}"
            : $"{fileCount} files, the largest is {Paths.HumanSize(largestBytes)}. The target applies to each file.", 13);
        intro.TextWrapping = TextWrapping.Wrap;
        intro.TextTrimming = TextTrimming.None;
        panel.Children.Add(intro);

        var valueBox = new TextBox { Text = Paths.Num(last.Value), Width = 90, Padding = new Thickness(6), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
        var percent = new RadioButton { Content = "% of original", GroupName = "unit", IsChecked = last.IsPercent, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var mb = new RadioButton { Content = "MB", GroupName = "unit", IsChecked = !last.IsPercent, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var preview = new TextBlock { Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        var error = new TextBlock { Foreground = Ui.Danger, FontSize = 12, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };

        // quick picks
        panel.Children.Add(new TextBlock { Text = "Quick pick", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 16, 0, 6) });
        var chips = new WrapPanel();
        foreach (var (value, isPercent) in new[] { (25.0, true), (50.0, true), (75.0, true), (1.0, false), (2.0, false), (5.0, false), (10.0, false) })
        {
            var label = isPercent ? $"{value:0}%" : $"{value:0} MB";
            var chip = Ui.Btn(null, label, () =>
            {
                valueBox.Text = Paths.Num(value);
                percent.IsChecked = isPercent;
                mb.IsChecked = !isPercent;
            });
            chip.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(chip);
        }
        panel.Children.Add(chips);

        // custom value
        panel.Children.Add(new TextBlock { Text = "Or type your own", Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(0, 10, 0, 6) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(valueBox);
        row.Children.Add(percent);
        row.Children.Add(mb);
        panel.Children.Add(row);
        panel.Children.Add(preview);
        panel.Children.Add(error);

        SizeTarget? Parse(out string? problem)
        {
            problem = null;
            var text = valueBox.Text.Trim().Replace(",", ".").TrimEnd('%').Trim();
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v <= 0)
            {
                problem = "Type a number, like 50 or 2.5.";
                return null;
            }
            bool isPct = percent.IsChecked == true;
            if (isPct && v >= 100) { problem = "Pick less than 100% (100% would be the same size)."; return null; }
            if (!isPct && fileCount == 1 && largestBytes > 0 && v * 1024 * 1024 >= largestBytes)
            {
                problem = $"The file is already {Paths.HumanSize(largestBytes)}. Pick a smaller size.";
                return null;
            }
            return new SizeTarget(v, isPct);
        }

        void Refresh()
        {
            error.Visibility = Visibility.Collapsed;
            var t = Parse(out _);
            if (t == null || largestBytes <= 0) { preview.Text = ""; return; }
            long bytes = t.BytesFor(largestBytes);
            preview.Text = t.IsPercent
                ? $"About {Paths.HumanSize(bytes)}" + (fileCount > 1 ? " for the largest file" : "")
                : $"About {Math.Round(100.0 * bytes / largestBytes)}% of the original" + (fileCount > 1 ? " (largest file)" : "");
        }
        valueBox.TextChanged += (_, _) => Refresh();
        percent.Checked += (_, _) => Refresh();
        mb.Checked += (_, _) => Refresh();
        Refresh();

        SizeTarget? result = null;
        void Accept()
        {
            var t = Parse(out var problem);
            if (t == null) { Show(error, problem ?? "Check the number."); return; }
            result = t;
            w.DialogResult = true;
        }
        var go = Ui.Btn(null, "Make smaller", Accept, primary: true);
        var cancel = Ui.Btn(null, "Cancel", () => w.DialogResult = false);
        panel.Children.Add(Ui.ButtonRow(cancel, go));
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        w.Content = panel;
        w.Loaded += (_, _) => { w.Activate(); valueBox.Focus(); valueBox.SelectAll(); };
        return w.ShowDialog() == true ? result : null;
    }

    private static void Show(TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
    }
}
