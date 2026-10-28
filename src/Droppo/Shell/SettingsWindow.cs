using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Droppo.Core;
using Droppo.Core.Studio;
using Droppo.Shell.Studio;

namespace Droppo.Shell;

/// <summary>Everything you can change, in one window. Nothing is saved until you press Save.</summary>
internal sealed class SettingsWindow : GlassWindow
{
    private readonly Settings _settings;

    private readonly ComboBox _trigger = new() { Width = 120, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly RadioButton _sameFolder = Radio("Next to the original file");
    private readonly RadioButton _oneFolder = Radio("Always in this folder:");
    private readonly RadioButton _ask = Radio("Ask me every time");
    private readonly TextBox _folder = Box(300);
    private readonly CheckBox _startup = new() { Content = "Start Droppo when Windows starts", FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ComboBox _dpi = new() { Width = 120, Height = 30, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _gifSeconds = Box(60);
    private readonly TextBox _ffmpeg = Box(300);
    private readonly TextBox _qpdf = Box(300);
    private readonly TextBox _libre = Box(300);
    private readonly TextBlock _ffmpegStatus = Status();
    private readonly TextBlock _qpdfStatus = Status();
    private readonly TextBlock _libreStatus = Status();
    private readonly TextBlock _signatureStatus = Status();
    private readonly TextBlock _error = Ui.Txt("", 12, color: Ui.Danger);
    private readonly CheckBox _glass = new() { Content = "Frosted glass windows", FontSize = 13 };
    private readonly CheckBox _motion = new() { Content = "Animations", FontSize = 13 };

    public bool Saved { get; private set; }

    public SettingsWindow(Settings settings)
    {
        _settings = settings;
        Title = "Droppo settings";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(24, 6, 24, 22) };
        panel.Children.Add(Ui.Header("Settings", $"Droppo {Tray.Version} \u00B7 {Brand.Tagline}"));

        // ---- wheel ----
        var wheel = new StackPanel();
        foreach (var k in new[] { "Shift", "Ctrl", "Alt" }) _trigger.Items.Add(k);
        _trigger.SelectedItem = string.Equals(settings.TriggerKey, "Ctrl", StringComparison.OrdinalIgnoreCase) ? "Ctrl"
            : string.Equals(settings.TriggerKey, "Alt", StringComparison.OrdinalIgnoreCase) ? "Alt" : "Shift";
        wheel.Children.Add(Row("Key to hold while dragging", _trigger));
        _startup.IsChecked = AutoStart.IsEnabled;
        _startup.Content = "Start Droppo when Windows starts";
        _startup.Margin = new Thickness(0, 8, 0, 0);
        wheel.Children.Add(_startup);
        Section(panel, "The wheel", wheel);

        // ---- results ----
        var output = new StackPanel();
        _sameFolder.IsChecked = settings.OutputMode == "SameFolder";
        _oneFolder.IsChecked = settings.OutputMode == "Folder";
        _ask.IsChecked = settings.OutputMode == "Ask";
        _folder.Text = settings.OutputFolder;
        output.Children.Add(_sameFolder);
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        folderRow.Children.Add(_oneFolder);
        _folder.Margin = new Thickness(10, 0, 4, 0);
        _folder.Width = 250;
        folderRow.Children.Add(_folder);
        folderRow.Children.Add(Ui.Btn(null, "Browse", BrowseOutput));
        output.Children.Add(folderRow);
        _folder.GotKeyboardFocus += (_, _) => _oneFolder.IsChecked = true;
        _ask.Margin = new Thickness(0, 8, 0, 0);
        output.Children.Add(_ask);
        output.Children.Add(Note("Your originals are never changed or overwritten. If a name is taken, Droppo adds a number, like \"photo (1)\"."));
        Section(panel, "Where should we save it?", output);

        // ---- conversions ----
        var conversions = new StackPanel();
        foreach (var d in new[] { 100, 150, 200, 300 }) _dpi.Items.Add(d + " dpi");
        _dpi.SelectedItem = settings.PdfImageDpi + " dpi";
        if (_dpi.SelectedItem == null) { _dpi.Items.Add(settings.PdfImageDpi + " dpi"); _dpi.SelectedItem = settings.PdfImageDpi + " dpi"; }
        conversions.Children.Add(Row("PDF pages to pictures, sharpness", _dpi));
        _gifSeconds.Text = settings.GifMaxSeconds.ToString(CultureInfo.InvariantCulture);
        var gif = new StackPanel { Orientation = Orientation.Horizontal };
        gif.Children.Add(_gifSeconds);
        gif.Children.Add(new TextBlock { Text = "seconds", FontSize = 13, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted });
        conversions.Children.Add(Row("Video to GIF uses the first", gif));
        Section(panel, "Conversions", conversions);

        // ---- look ----
        var look = new StackPanel();
        _glass.IsChecked = settings.GlassEffects;
        _motion.IsChecked = settings.Animations;
        look.Children.Add(_glass);
        _motion.Margin = new Thickness(0, 8, 0, 0);
        look.Children.Add(_motion);
        look.Children.Add(Note("Glass needs Windows 11 and \"Transparency effects\" on. Changes show on windows opened after saving."));
        Section(panel, "Look and feel", look);

        // ---- helper programs ----
        var tools = new StackPanel();
        tools.Children.Add(Note("Droppo comes with these. Only pick a different copy if you know you need one."));
        tools.Children.Add(ToolRow("FFmpeg (pictures, video, audio)", _ffmpeg, _ffmpegStatus, settings.FfmpegPath, "ffmpeg.exe"));
        tools.Children.Add(ToolRow("qpdf (PDF tools)", _qpdf, _qpdfStatus, settings.QpdfPath, "qpdf.exe"));
        tools.Children.Add(ToolRow("LibreOffice (Word/Excel/PowerPoint to PDF, if no Microsoft Office)", _libre, _libreStatus, settings.LibreOfficePath, "soffice.exe"));
        RefreshTools();
        foreach (var box in new[] { _ffmpeg, _qpdf, _libre }) box.LostKeyboardFocus += (_, _) => RefreshTools();
        Section(panel, "Helper programs", tools);

        // ---- signature ----
        var sig = new DockPanel { LastChildFill = false };
        var sigButtons = new StackPanel { Orientation = Orientation.Horizontal };
        sigButtons.Children.Add(Ui.Btn(Ui.Glyph.Pen, "Draw new", DrawSignature));
        sigButtons.Children.Add(Ui.Btn(Ui.Glyph.Delete, "Delete", DeleteSignature));
        DockPanel.SetDock(_signatureStatus, Dock.Left);
        DockPanel.SetDock(sigButtons, Dock.Right);
        sig.Children.Add(_signatureStatus);
        sig.Children.Add(sigButtons);
        Section(panel, "Your signature", sig);
        RefreshSignature();

        // ---- footer ----
        _error.Margin = new Thickness(0, 14, 0, 0);
        _error.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(_error);
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var links = new StackPanel { Orientation = Orientation.Horizontal };
        links.Children.Add(Ui.Btn(null, "Open log", () => Open(Log.FilePath)));
        links.Children.Add(Ui.Btn(null, "Settings folder", () => Open(Settings.Folder)));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Ui.Btn(null, "Cancel", Close));
        buttons.Children.Add(Ui.Btn(Ui.Glyph.Save, "Save", SaveAndClose, primary: true));
        DockPanel.SetDock(links, Dock.Left);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(links);
        footer.Children.Add(buttons);
        panel.Children.Add(footer);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 820 };
        Loaded += (_, _) => { Activate(); Motion.Rise(panel, 8, 160); };
    }

    // ---------- actions ----------

    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save results in this folder" };
        if (Directory.Exists(_folder.Text)) dialog.InitialDirectory = _folder.Text;
        if (dialog.ShowDialog(this) != true) return;
        _folder.Text = dialog.FolderName;
        _oneFolder.IsChecked = true;
    }

    private void BrowseTool(TextBox box, string exe)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = $"{exe}|{exe}|Programs|*.exe", Title = "Find " + exe };
        if (dialog.ShowDialog(this) != true) return;
        box.Text = dialog.FileName;
        RefreshTools();
    }

    private void RefreshTools()
    {
        SetToolStatus(_ffmpegStatus, FFmpeg.TryCreate(Clean(_ffmpeg.Text))?.ExePath, "Not found. Reinstalling Droppo puts it back.");
        SetToolStatus(_qpdfStatus, Qpdf.TryCreate(Clean(_qpdf.Text))?.ExePath, "Not found. Reinstalling Droppo puts it back.");
        SetToolStatus(_libreStatus, LibreOfficeConverter.TryCreate(Clean(_libre.Text))?.ExePath, "Not installed. Only needed without Microsoft Office: free from libreoffice.org.");
    }

    private static void SetToolStatus(TextBlock status, string? found, string missing)
    {
        status.Text = found != null ? "Ready: " + found : missing;
        status.Foreground = found != null ? Ui.Success : Ui.Danger;
        status.ToolTip = status.Text;
    }

    private void RefreshSignature() =>
        _signatureStatus.Text = SavedSignature.Load() != null ? "Saved on this PC. Use the Sign tool in PDF Studio." : "None saved yet.";

    private void DrawSignature()
    {
        Dialogs.DrawSignature(this);
        RefreshSignature();
    }

    private void DeleteSignature()
    {
        if (SavedSignature.Load() == null) return;
        if (MessageBox.Show(this, "Delete your saved signature?", "Signature", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { SavedSignature.Delete(); }
        catch (Exception ex) { _error.Text = "Couldn't delete it: " + ex.Message; }
        RefreshSignature();
    }

    private void SaveAndClose()
    {
        if (!int.TryParse(_gifSeconds.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int gif) || gif < 1 || gif > 600)
        {
            _error.Text = "GIF length should be a whole number of seconds between 1 and 600.";
            return;
        }
        string mode = _oneFolder.IsChecked == true ? "Folder" : _ask.IsChecked == true ? "Ask" : "SameFolder";
        string folder = Clean(_folder.Text) ?? "";
        if (mode == "Folder")
        {
            if (folder.Length == 0 || !Path.IsPathRooted(folder)) { _error.Text = "Choose the folder to save results in."; return; }
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { _error.Text = "Can't use that folder: " + ex.Message; return; }
        }

        _settings.TriggerKey = _trigger.SelectedItem as string ?? "Shift";
        _settings.OutputMode = mode;
        if (folder.Length > 0) _settings.OutputFolder = folder;
        _settings.PdfImageDpi = int.TryParse((_dpi.SelectedItem as string)?.Split(' ')[0], out int dpi) ? dpi : 150;
        _settings.GifMaxSeconds = gif;
        _settings.FfmpegPath = Clean(_ffmpeg.Text);
        _settings.QpdfPath = Clean(_qpdf.Text);
        _settings.LibreOfficePath = Clean(_libre.Text);
        _settings.GlassEffects = _glass.IsChecked == true;
        _settings.Animations = _motion.IsChecked == true;
        _settings.Save();

        bool wantStartup = _startup.IsChecked == true;
        if (wantStartup != AutoStart.IsEnabled)
        {
            try { AutoStart.Set(wantStartup); }
            catch (Exception ex) { Log.Write("Start with Windows failed: " + ex.Message); }
        }

        Saved = true;
        Close();
    }

    private static void Open(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
            else
            {
                if (!File.Exists(path)) File.WriteAllText(path, "");
                Process.Start("notepad.exe", $"\"{path}\"");
            }
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't open " + path + ": " + ex.Message);
        }
    }

    // ---------- layout helpers ----------

    private static string? Clean(string? text)
    {
        var t = text?.Trim().Trim('"');
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private UIElement ToolRow(string label, TextBox box, TextBlock status, string? value, string exe)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Ui.Txt(label, 13));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        box.Text = value ?? "";
        row.Children.Add(box);
        row.Children.Add(Ui.Btn(null, "Browse", () => BrowseTool(box, exe)));
        panel.Children.Add(row);
        status.Margin = new Thickness(0, 3, 0, 0);
        panel.Children.Add(status);
        return panel;
    }

    /// <summary>A titled card.</summary>
    private static void Section(Panel parent, string title, UIElement content)
    {
        var heading = Ui.Txt(title, 13.5, bold: true);
        heading.Margin = new Thickness(2, 20, 0, 8);
        parent.Children.Add(heading);
        parent.Children.Add(Ui.CardPanel(content));
    }

    private static TextBlock Note(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
    };

    private static TextBlock Status() => new()
    {
        FontSize = 12, Foreground = Ui.Muted, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
    };

    private static RadioButton Radio(string text) => new()
    {
        Content = text, GroupName = "output", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static TextBox Box(double width) => new()
    {
        Width = width, Height = 30, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 0, 4, 0),
    };

    private static UIElement Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = Ui.Txt(label, 13);
        grid.Children.Add(text);
        Grid.SetColumn((FrameworkElement)control, 1);
        grid.Children.Add(control);
        return grid;
    }
}
