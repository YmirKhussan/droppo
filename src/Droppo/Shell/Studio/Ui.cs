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
    private static readonly Brush SolidBackground = Solid(0xF4, 0xF6, 0xFA);   // Cloud White
    private static readonly Brush GlassBackground = Solid(0x59, 0xF4, 0xF6, 0xFA);
    private static readonly Brush SolidSurface = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush GlassSurface = Solid(0xB8, 0xFF, 0xFF, 0xFF);
    private static readonly Brush SolidCard = Solid(0xFF, 0xFF, 0xFF);
    private static readonly Brush GlassCard = Solid(0xE0, 0xFF, 0xFF, 0xFF);

    /// <summary>Window / canvas background.</summary>
    public static Brush Background => Glass.Enabled ? GlassBackground : SolidBackground;
    /// <summary>Bars and panels.</summary>
    public static Brush Surface => Glass.Enabled ? GlassSurface : SolidSurface;
    /// <summary>Cards and anything with text that must stay crisp.</summary>
    public static Brush Card => Glass.Enabled ? GlassCard : SolidCard;

    public static readonly Brush Line = Solid(0x26, 0x1C, 0x2C, 0x49);      // navy at 15%: works on solid and glass
    public static readonly Brush Text = Solid(0x1C, 0x2C, 0x49);            // Deep Navy
    public static readonly Brush Muted = Solid(0x65, 0x70, 0x87);           // Slate Grey
    public static readonly Brush Accent = Solid(0x28, 0x57, 0xC5);          // Droppo Cobalt
    public static readonly Brush AccentSoft = Solid(0xDC, 0xE7, 0xFF);      // Pale Blue
    public static readonly Brush Hover = Solid(0xEE, 0xF3, 0xFD);
    public static readonly Brush Danger = Solid(0xC9, 0x3A, 0x3A);
    public static readonly Brush Success = Solid(0x1E, 0x8E, 0x5A);
    public static readonly Brush Shade = Solid(0x99, 0xF4, 0xF6, 0xFA);

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
            Background = primary ? Accent : Card,
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
        var w = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Background = SolidSurface, Foreground = Text, FontFamily = Body, ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            UseLayoutRounding = true,
        };
        if (owner != null) w.Owner = owner;
        else w.Topmost = true; // asked from the wheel while Explorer has focus: make sure it's seen
        Glass.Apply(w, acrylic: true);
        w.Loaded += (_, _) => { if (w.Content is UIElement content) Motion.Rise(content, 6, 140); };
        return w;
    }

    /// <summary>A rounded panel for grouping things (settings sections, previews).</summary>
    public static Border CardPanel(UIElement child, Thickness? padding = null) => new()
    {
        Child = child, Background = Card, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10), Padding = padding ?? new Thickness(16, 14, 16, 14),
    };

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
