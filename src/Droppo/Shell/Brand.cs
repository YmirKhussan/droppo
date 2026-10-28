using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPath = System.Windows.Shapes.Path;

namespace Droppo.Shell;

/// <summary>
/// Droppo's colours and logo, drawn as vectors so they stay sharp at any size or scaling.
/// The same shapes are in the brand folder as SVG (brand/droppo-icon.svg, droppo-symbol.svg, droppo-wordmark.svg).
/// </summary>
internal static class Brand
{
    public static readonly Color Cobalt = Color.FromRgb(0x38, 0x82, 0xF6);      // Cobalt Blue: actions, highlights
    public static readonly Color CobaltDeep = Color.FromRgb(0x24, 0x59, 0xD9);
    public static readonly Color CobaltLight = Color.FromRgb(0x5D, 0xB0, 0xFF);
    public static readonly Color Navy = Color.FromRgb(0x0F, 0x2D, 0x5B);        // Deep Navy: text, headings
    public static readonly Color Ice = Color.FromRgb(0xE8, 0xF1, 0xFF);         // Ice Blue: hover, selected
    public static readonly Color Slate = Color.FromRgb(0x64, 0x74, 0x8B);       // Slate Grey: secondary text
    public static readonly Color Cloud = Color.FromRgb(0xF4, 0xF7, 0xFC);
    public static readonly Color Success = Color.FromRgb(0x22, 0xB5, 0x73);

    public const string Tagline = "Drop it. Done.";

    // the mark on a 24-unit grid: a bold "D" with an arrow cut through it, pointing right
    private const string GlyphData = "M7.58,4.61 L7.37,4.64 L7.16,4.69 L6.96,4.77 L6.76,4.86 L6.58,4.97 L6.4,5.1 L6.24,5.24 L6.1,5.4 L5.97,5.58 L5.86,5.76 L5.77,5.96 L5.69,6.16 L5.64,6.37 L5.61,6.58 L5.6,6.8 L5.6,10.35 L9.6,10.35 L9.6,9.55 L9.67,9.29 L9.76,9.13 L9.94,8.94 L10.17,8.8 L10.43,8.74 L10.69,8.76 L10.86,8.82 L11.02,8.9 L14.43,11.32 L14.61,11.53 L14.69,11.69 L14.74,11.95 L14.73,12.14 L14.69,12.31 L14.61,12.47 L14.5,12.62 L14.36,12.73 L11.02,15.1 L10.78,15.22 L10.61,15.26 L10.43,15.26 L10.17,15.2 L9.94,15.06 L9.76,14.87 L9.67,14.71 L9.6,14.45 L9.6,13.65 L5.6,13.65 L5.61,17.42 L5.64,17.63 L5.69,17.84 L5.77,18.04 L5.86,18.24 L5.97,18.42 L6.1,18.6 L6.24,18.76 L6.4,18.9 L6.58,19.03 L6.76,19.14 L6.96,19.23 L7.16,19.31 L7.37,19.36 L7.58,19.39 L7.8,19.4 L12.23,19.4 L12.64,19.39 L13.3,19.32 L13.68,19.25 L14.17,19.13 L14.8,18.93 L15.26,18.74 L15.85,18.44 L16.18,18.24 L16.5,18.02 L16.81,17.79 L17.11,17.54 L17.39,17.27 L17.66,16.99 L17.91,16.7 L18.22,16.3 L18.44,15.98 L18.64,15.65 L18.94,15.06 L19.09,14.71 L19.22,14.34 L19.33,13.97 L19.42,13.59 L19.5,13.21 L19.55,12.83 L19.59,12.44 L19.6,12.06 L19.59,11.67 L19.56,11.28 L19.5,10.79 L19.36,10.14 L19.25,9.77 L19.13,9.4 L18.98,9.04 L18.82,8.69 L18.64,8.35 L18.44,8.02 L18.15,7.6 L17.91,7.3 L17.66,7.01 L17.19,6.54 L16.81,6.21 L16.28,5.83 L15.95,5.62 L15.61,5.43 L15.26,5.26 L14.91,5.11 L14.43,4.95 L13.79,4.78 L13.41,4.7 L13.03,4.65 L12.53,4.61 L7.8,4.6 Z";

    // "droppo" set in Poppins Bold, tightened, outlined. 3572.0 x 1006.0 units; top of the "d" at y = 0, baseline at 740.0.
    private const string WordmarkData = "F1 M 246,174 Q 301,174 346.5,197 Q 392,220 418,259 V 0 H 589 V 740 H 418 V 660 Q 394,700 349.5,724 Q 305,748 246,748 Q 177,748 121,712.5 Q 65,677 32.5,611.5 Q 0,546 0,460 Q 0,374 32.5,309 Q 65,244 121,209 Q 177,174 246,174 Z M 296,323 Q 245,323 209.5,359.5 Q 174,396 174,460 Q 174,524 209.5,561.5 Q 245,599 296,599 Q 347,599 382.5,562 Q 418,525 418,461 Q 418,397 382.5,360 Q 347,323 296,323 Z M 1037,176 V 357 H 990 Q 926,357 894,384.5 Q 862,412 862,481 V 740 H 691 V 182 H 862 V 275 Q 892,229 937,202.5 Q 982,176 1037,176 Z M 1063,461 Q 1063,375 1101,309.5 Q 1139,244 1205,209 Q 1271,174 1353,174 Q 1435,174 1501,209 Q 1567,244 1605,309.5 Q 1643,375 1643,461 Q 1643,547 1604.5,612.5 Q 1566,678 1499.5,713 Q 1433,748 1351,748 Q 1269,748 1203.5,713 Q 1138,678 1100.5,613 Q 1063,548 1063,461 Z M 1469,461 Q 1469,394 1435.5,358 Q 1402,322 1353,322 Q 1303,322 1270,357.5 Q 1237,393 1237,461 Q 1237,528 1269.5,564 Q 1302,600 1351,600 Q 1400,600 1434.5,564 Q 1469,528 1469,461 Z M 2055,174 Q 2124,174 2180,209 Q 2236,244 2268.5,309 Q 2301,374 2301,460 Q 2301,546 2268.5,611.5 Q 2236,677 2180,712.5 Q 2124,748 2055,748 Q 1997,748 1952.5,724 Q 1908,700 1883,662 V 1006 H 1712 V 182 H 1883 V 261 Q 1908,222 1952,198 Q 1996,174 2055,174 Z M 2004,323 Q 1953,323 1917.5,360 Q 1882,397 1882,461 Q 1882,525 1917.5,562 Q 1953,599 2004,599 Q 2055,599 2091,561.5 Q 2127,524 2127,460 Q 2127,396 2091.5,359.5 Q 2056,323 2004,323 Z M 2712,174 Q 2781,174 2837,209 Q 2893,244 2925.5,309 Q 2958,374 2958,460 Q 2958,546 2925.5,611.5 Q 2893,677 2837,712.5 Q 2781,748 2712,748 Q 2654,748 2609.5,724 Q 2565,700 2540,662 V 1006 H 2369 V 182 H 2540 V 261 Q 2565,222 2609,198 Q 2653,174 2712,174 Z M 2661,323 Q 2610,323 2574.5,360 Q 2539,397 2539,461 Q 2539,525 2574.5,562 Q 2610,599 2661,599 Q 2712,599 2748,561.5 Q 2784,524 2784,460 Q 2784,396 2748.5,359.5 Q 2713,323 2661,323 Z M 2992,461 Q 2992,375 3030,309.5 Q 3068,244 3134,209 Q 3200,174 3282,174 Q 3364,174 3430,209 Q 3496,244 3534,309.5 Q 3572,375 3572,461 Q 3572,547 3533.5,612.5 Q 3495,678 3428.5,713 Q 3362,748 3280,748 Q 3198,748 3132.5,713 Q 3067,678 3029.5,613 Q 2992,548 2992,461 Z M 3398,461 Q 3398,394 3364.5,358 Q 3331,322 3282,322 Q 3232,322 3199,357.5 Q 3166,393 3166,461 Q 3166,528 3198.5,564 Q 3231,600 3280,600 Q 3329,600 3363.5,564 Q 3398,528 3398,461 Z";
    private const double WordmarkWidth = 3572.0, WordmarkHeight = 1006.0, WordmarkCap = 740.0;

    private static Geometry? _glyph, _wordmark;
    private static Geometry GlyphGeometry => _glyph ??= Frozen(Geometry.Parse(GlyphData));
    private static Geometry WordmarkGeometry => _wordmark ??= Frozen(Geometry.Parse(WordmarkData));

    private static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Brush? _tileFill, _gloss;

    /// <summary>The icon's blue: light at the top-left, deeper at the bottom-right.</summary>
    public static Brush TileFill => _tileFill ??= Freeze(new LinearGradientBrush(new GradientStopCollection
    {
        new GradientStop(CobaltLight, 0), new GradientStop(Cobalt, 0.55), new GradientStop(CobaltDeep, 1),
    }, new Point(0, 0), new Point(1, 1)));

    private static Brush Gloss => _gloss ??= Freeze(new LinearGradientBrush(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), new Point(0, 0), new Point(0, 0.5)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>The mark alone (the "D" with the arrow), size x size DIPs.</summary>
    public static FrameworkElement Symbol(double size, Brush color)
    {
        var path = new WpfPath { Data = GlyphGeometry, Fill = color };
        var canvas = new Canvas { Width = 18, Height = 18 };
        Canvas.SetLeft(path, -4); // the glyph's ink sits at 4..22 x 3..21 on its grid
        Canvas.SetTop(path, -3);
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>The app icon: white mark on the blue gradient tile.</summary>
    public static FrameworkElement Tile(double size)
    {
        var radius = new CornerRadius(size * 0.235);
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new Border { CornerRadius = radius, Background = TileFill });
        grid.Children.Add(new Border { CornerRadius = radius, Background = Gloss, BorderThickness = new Thickness(size >= 28 ? 1 : 0), BorderBrush = Brush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)) });
        var glyph = Symbol(size * 0.6, Brushes.White);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        glyph.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(glyph);
        return grid;
    }

    /// <summary>"droppo", with the "d" capHeight DIPs tall.</summary>
    public static FrameworkElement Wordmark(double capHeight, Brush color)
    {
        double k = capHeight / WordmarkCap;
        var path = new WpfPath { Data = WordmarkGeometry, Fill = color };
        var canvas = new Canvas { Width = WordmarkWidth, Height = WordmarkHeight };
        canvas.Children.Add(path);
        return new Viewbox
        {
            Width = WordmarkWidth * k, Height = WordmarkHeight * k, Child = canvas, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    /// <summary>Icon + wordmark (+ tagline), like the logo on the brand board.</summary>
    public static FrameworkElement Lockup(double height, bool onDark = false, bool tagline = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var tile = Tile(height);
        tile.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(tile);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(height * 0.24, 0, 0, 0) };
        texts.Children.Add(Wordmark(height * (tagline ? 0.42 : 0.5), onDark ? Brushes.White : Brush(Navy)));
        if (tagline)
            texts.Children.Add(new TextBlock
            {
                Text = Tagline, FontSize = height * 0.2, FontWeight = FontWeights.SemiBold, Margin = new Thickness(1, height * 0.02, 0, 0),
                Foreground = onDark ? Brush(Color.FromRgb(0xDC, 0xE9, 0xFF)) : Brush(Cobalt),
            });
        row.Children.Add(texts);
        return row;
    }
}
