using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Droppo.Core.Studio;
using Droppo.Shell;

namespace Droppo.Shell.Studio;

/// <summary>
/// Colours, fonts and small control builders, so every window looks the same. Colours follow the Droppo
/// brand: cobalt for actions, navy for text, cloud white surfaces. With glass on (Windows 11), surfaces
/// become see-through so the frosted window background shows.
/// </summary>
internal static class Ui
{
    private static readonly Brush SolidBackground = Solid(0xF4, 0xF7, 0xFC);   // Cloud
    private static readonly Brush SolidSurface = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush GlassButton = Solid(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Brush GlassTile = Solid(0xCC, 0xFF, 0xFF, 0xFF);

    /// <summary>Canvas areas inside a window: see-through with glass on, so the backdrop shows.</summary>
    public static Brush Background => Glass.Enabled ? Brushes.Transparent : SolidBackground;
    /// <summary>Bars and panels.</summary>
    public static Brush Surface => Glass.BarBackground;
    /// <summary>Cards and anything with text that must stay crisp.</summary>
    public static Brush Card => Glass.CardBackground;
    /// <summary>Normal (not primary) buttons.</summary>
    public static Brush ButtonFill => Glass.Enabled ? GlassButton : SolidSurface;
    /// <summary>Edges of glass bars: a line of light with glass on, a hairline without.</summary>
    public static Brush Edge => Glass.Enabled ? GlassEdge : Line;
    private static readonly Brush GlassEdge = Solid(0xE6, 0xFF, 0xFF, 0xFF);

    /// <summary>Page tiles in Studio.</summary>
    public static Brush TileFill => Glass.Enabled ? GlassTile : Brushes.Transparent;

    public static readonly Brush Line = Solid(0x24, 0x0F, 0x2D, 0x5B);      // navy at 14%: works on solid and glass
    public static readonly Brush Text = Solid(0x0F, 0x2D, 0x5B);            // Deep Navy
    public static readonly Brush Muted = Solid(0x64, 0x74, 0x8B);           // Slate Grey
    public static readonly Brush Accent = Solid(0x38, 0x82, 0xF6);          // Cobalt Blue
    public static readonly Brush AccentSoft = Solid(0xE8, 0xF1, 0xFF);      // Ice Blue
    public static readonly Brush Hover = Solid(0xF0, 0xF6, 0xFF);
    public static readonly Brush Danger = Solid(0xE5, 0x48, 0x4D);
    public static readonly Brush Success = Solid(0x22, 0xB5, 0x73);
    public static readonly Brush Shade = Solid(0x99, 0xF4, 0xF7, 0xFC);

    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily Body = new("Segoe UI");
    public static readonly FontFamily PageText = new("Arial"); // same widths as the PDF's Helvetica

    public static class Glyph
    {
        public const string Add = "", Delete = "", Save = "", More = "", Undo = "",
            Edit = "", Back = "", Forward = "", RotateRight = "", Lock = "", ZoomIn = "",
            ZoomOut = "", Picture = "", Pen = "", Highlight = "", Font = "",
            Select = "", Eraser = "", Copy = "", Page = "", Replace = "",
            Sign = "", Form = "", Settings = "", Folder = "";
    }

    public static SolidColorBrush Solid(byte r, byte g, byte b) => Solid(0xFF, r, g, b);

    public static SolidColorBrush Solid(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Solid(Rgb c) => Solid(c.R, c.G, c.B);

    /// <summary>Flat button: optional icon + label. Primary = filled accent.</summary>
    public static Button Btn(string? glyph, string? label, Action onClick, bool primary = false, string? tooltip = null)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (glyph != null)
            content.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        if (label != null)
            content.Children.Add(new TextBlock
            {
                Text = label, FontFamily = Body, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(glyph != null ? 7 : 0, 0, 0, 0),
            });

        var button = new Button
        {
            Content = content,
            Padding = new Thickness(label != null ? 14 : 9, 7, label != null ? 14 : 9, 7),
            Margin = new Thickness(3, 0, 3, 0),
            Background = primary ? Accent : ButtonFill,
            Foreground = primary ? Brushes.White : Text,
            BorderBrush = primary ? Accent : Line,
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = tooltip ?? label,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static TextBlock Txt(string text, double size = 13, bool bold = false, Brush? color = null) => new()
    {
        Text = text, FontFamily = Body, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = color ?? Text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>Small round colour swatch button.</summary>
    public static Border Swatch(Rgb color, Action onClick, string tooltip)
    {
        var swatch = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(3, 0, 3, 0),
            Background = Solid(color), BorderBrush = Line, BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
            ToolTip = tooltip,
        };
        swatch.MouseLeftButtonUp += (_, _) => onClick();
        return swatch;
    }

    public static Window Dialog(string title, Window? owner)
    {
        var w = new GlassWindow(card: true)
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
        };
        if (owner != null) w.Owner = owner;
        else w.Topmost = true; // asked from the wheel while Explorer has focus: make sure it's seen
        return w;
    }

    /// <summary>A frosted glass card for grouping things (settings sections, previews).</summary>
    public static Border CardPanel(UIElement child, Thickness? padding = null) => Glass.Card(child, padding, 14);

    /// <summary>A pill you can click: quick picks like "50%" or "2 MB". <see cref="SetChip"/> marks the chosen one.</summary>
    public static Border Chip(string label, Action onClick)
    {
        var text = new TextBlock { Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Text, HorizontalAlignment = HorizontalAlignment.Center };
        var chip = new Border
        {
            Child = text, Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(0, 0, 8, 8), MinWidth = 58,
            CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
        };
        SetChip(chip, false);
        chip.MouseEnter += (_, _) => { if (!Equals(chip.Tag, true)) chip.Background = Hover; };
        chip.MouseLeave += (_, _) => SetChip(chip, Equals(chip.Tag, true));
        chip.MouseLeftButtonUp += (_, _) => onClick();
        return chip;
    }

    public static void SetChip(Border chip, bool selected)
    {
        chip.Tag = selected;
        chip.Background = selected ? Accent : ButtonFill;
        chip.BorderBrush = selected ? Accent : Line;
        if (chip.Child is TextBlock t) t.Foreground = selected ? Brushes.White : Text;
    }

    /// <summary>Window header: the Droppo mark and a title.</summary>
    public static StackPanel Header(string title, string? subtitle = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = Brand.Tile(30);
        mark.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(mark);
        var texts = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Txt(title, 16, bold: true));
        if (subtitle != null) texts.Children.Add(Txt(subtitle, 12, color: Muted));
        row.Children.Add(texts);
        return row;
    }

    public static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }
}
