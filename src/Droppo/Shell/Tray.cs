using System;
using System.Diagnostics;
using System.IO;
using Droppo.Core;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Droppo.Shell;

/// <summary>The icon by the clock and its menu. (Results are shown by <see cref="Toast"/>; balloons are the fallback.)</summary>
internal sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Settings _settings;
    private string? _revealOnClick;

    public event Action? ExitRequested;
    public event Action? TestWheelRequested;
    public event Action? StudioRequested;
    public event Action? SettingsRequested;
    public event Action? HomeRequested;

    /// <summary>Shows a short message the Droppo way (set by the app).</summary>
    public Action<ToastKind, string, string>? Say { get; set; }

    public Tray(Settings settings)
    {
        _settings = settings;
        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Droppo: drag a file, hold " + settings.TriggerKey,
            Visible = true,
        };
        _icon.ContextMenuStrip = new Forms.ContextMenuStrip
        {
            Renderer = new MenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.75f), Padding = new Forms.Padding(4, 6, 4, 6),
        };
        _icon.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        RebuildMenu();

        _icon.BalloonTipClicked += (_, _) => Reveal(_revealOnClick);
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) HomeRequested?.Invoke();
        };
    }

    public void SetBusy(bool busy) => _icon.Text = busy ? "Droppo: working on it\u2026" : "Droppo: drag a file, hold " + _settings.TriggerKey;

    /// <summary>Shows a Windows notification. Clicking it opens the folder with <paramref name="reveal"/> selected.</summary>
    public void Notify(string title, string text, bool error = false, string? reveal = null)
    {
        _revealOnClick = reveal;
        if (string.IsNullOrWhiteSpace(text)) text = " ";
        if (text.Length > 240) text = text[..237] + "...";
        _icon.ShowBalloonTip(4000, title, text, error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    public static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Write("Could not open Explorer: " + ex.Message);
        }
    }

    private void RebuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;
        menu.Items.Clear();

        menu.Items.Add(new Forms.ToolStripMenuItem($"Droppo {Version}")
        {
            Enabled = false, Font = new Drawing.Font("Segoe UI Semibold", 9.75f), Tag = MenuRenderer.TitleTag,
        });
        menu.Items.Add(new Forms.ToolStripMenuItem($"Drag a file, then hold {_settings.TriggerKey}") { Enabled = false, Tag = MenuRenderer.HeaderTag });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Open Droppo", () => HomeRequested?.Invoke()));
        menu.Items.Add(Item("Open PDF Studio", () => StudioRequested?.Invoke()));
        menu.Items.Add(Item("Test the wheel", () => TestWheelRequested?.Invoke()));
        menu.Items.Add(Item("Settings", () => SettingsRequested?.Invoke()));

        var undo = Item("Undo last rename", UndoRename);
        undo.Enabled = File.Exists(RenameOps.UndoFile);
        menu.Items.Add(undo);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Open log", () => OpenInNotepad(Log.FilePath)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quit Droppo", () => ExitRequested?.Invoke()));
    }

    public static string Version =>
        typeof(Tray).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "";

    private void UndoRename()
    {
        try
        {
            int n = RenameOps.UndoLast();
            Tell(n == 0 ? ToastKind.Info : ToastKind.Done, n == 0 ? "Nothing to undo" : "Rename undone.",
                 n == 0 ? "Those files were moved or renamed since." : $"{n} file{(n == 1 ? "" : "s")} got their old names back.");
        }
        catch (Exception ex)
        {
            Log.Write("Undo rename failed: " + ex);
            Tell(ToastKind.Error, "Couldn't undo the rename.", ex.Message);
        }
    }

    private void Tell(ToastKind kind, string title, string body)
    {
        if (Say != null) Say(kind, title, body);
        else Notify(title, body, error: kind == ToastKind.Error);
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text) { Padding = new Forms.Padding(6, 5, 6, 5) };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>Paints the tray menu in Droppo's colours: white, rounded pale-blue highlight, navy text.</summary>
    private sealed class MenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        public const string HeaderTag = "header", TitleTag = "title";
        private static readonly Drawing.Color Navy = Drawing.Color.FromArgb(0x0F, 0x2D, 0x5B);
        private static readonly Drawing.Color Slate = Drawing.Color.FromArgb(0x64, 0x74, 0x8B);
        private static readonly Drawing.Color Highlight = Drawing.Color.FromArgb(0xE8, 0xF1, 0xFF);
        private static readonly Drawing.Color Edge = Drawing.Color.FromArgb(0xDD, 0xE3, 0xEE);
        private static readonly Drawing.Color Rule = Drawing.Color.FromArgb(0xE3, 0xE8, 0xF0);

        public MenuRenderer() { RoundedEdges = true; }

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(Drawing.Color.White);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Edge);
            var r = e.AffectedBounds;
            e.Graphics.DrawRectangle(pen, new Drawing.Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1));
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var r = new Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = Rounded(r, 6);
            using var brush = new Drawing.SolidBrush(Highlight);
            e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = Equals(e.Item.Tag, TitleTag) ? Navy : Equals(e.Item.Tag, HeaderTag) || !e.Item.Enabled ? Slate : Navy;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Rule);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        private static Drawing2D.GraphicsPath Rounded(Drawing.Rectangle r, int radius)
        {
            int d = radius * 2;
            var path = new Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private static void OpenInNotepad(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start("notepad.exe", $"\"{path}\"");
        }
        catch (Exception ex)
        {
            Log.Write("Could not open Notepad: " + ex.Message);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Write("Icon failed to load: " + ex.Message);
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
