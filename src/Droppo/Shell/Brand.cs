using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPath = System.Windows.Shapes.Path;

namespace Droppo.Shell;

/// <summary>
/// Droppo's colours and logo, drawn as vectors so they stay sharp at any size or scaling.
/// The same shapes are in the brand folder as SVG (brand/droppo-symbol.svg, droppo-wordmark.svg).
/// </summary>
internal static class Brand
{
    public static readonly Color Cobalt = Color.FromRgb(0x28, 0x57, 0xC5);
    public static readonly Color CobaltDark = Color.FromRgb(0x1F, 0x46, 0xA3);
    public static readonly Color Navy = Color.FromRgb(0x1C, 0x2C, 0x49);
    public static readonly Color Cloud = Color.FromRgb(0xF4, 0xF6, 0xFA);
    public static readonly Color Slate = Color.FromRgb(0x65, 0x70, 0x87);
    public static readonly Color PaleBlue = Color.FromRgb(0xDC, 0xE7, 0xFF);

    public const string Tagline = "Drop it. Done.";

    // the mark on a 24-unit grid: a document whose outline opens into an arrow
    private const string SymbolData =
        "M8.5,9.5 V5.75 A2.75,2.75 0 0 1 11.25,3 H14.6 L19.75,8.15 V18.25 A2.75,2.75 0 0 1 17,21 H11.25 " +
        "A2.75,2.75 0 0 1 8.5,18.25 V16.5 M14.6,3 V6.15 A2,2 0 0 0 16.6,8.15 H19.75 " +
        "M6.75,10.25 L4,13 L6.75,15.75 M4,13 H14.5";

    // "droppo" set in Nunito ExtraBold, tightened, outlined. 3262 x 902 units; top of the "d" at y = 0, baseline at 713.
    private const string WordmarkData = "F1 M 215,724 Q 151,724 102.5,693 Q 54,662 27,604 Q 0,546 0,467 Q 0,388 27,331 Q 54,274 102.5,243 Q 151,212 215,212 Q 273,212 318,240 Q 350,260 367,289 V 76 Q 367,38 386,18.5 Q 405,-1 442,-1 Q 478,-1 498,18.5 Q 518,38 518,76 V 645 Q 518,682 498.5,702 Q 479,722 443,722 Q 407,722 387.5,702 Q 368,682 368,645 V 643 Q 352,674 320,694 Q 274,724 215,724 Z M 260,611 Q 293,611 317,595 Q 341,579 355,547.5 Q 369,516 369,467 Q 369,394 339,359.5 Q 309,325 260,325 Q 228,325 203.5,340 Q 179,355 165.5,386.5 Q 152,418 152,467 Q 152,540 182,575.5 Q 212,611 260,611 Z M 694,722 Q 656,722 636,702 Q 616,682 616,645 V 290 Q 616,253 635.5,233.5 Q 655,214 690,214 Q 726,214 745,233.5 Q 764,253 764,290 V 308 Q 778,272 807,249 Q 848,216 913,212 Q 938,210 951.5,224.5 Q 965,239 966,272 Q 968,303 953.5,321 Q 939,339 905,343 L 882,345 Q 825,350 797.5,378 Q 770,406 770,459 V 645 Q 770,682 750.5,702 Q 731,722 694,722 Z M 1246,724 Q 1167,724 1108.5,693 Q 1050,662 1018,604 Q 986,546 986,467 Q 986,408 1004,361 Q 1022,314 1056.5,280.5 Q 1091,247 1139,229.5 Q 1187,212 1246,212 Q 1325,212 1383.5,243 Q 1442,274 1474.5,331 Q 1507,388 1507,467 Q 1507,527 1488.5,574 Q 1470,621 1436,655 Q 1402,689 1353.5,706.5 Q 1305,724 1246,724 Z M 1246,611 Q 1279,611 1303,595 Q 1327,579 1341,547.5 Q 1355,516 1355,467 Q 1355,394 1325,359.5 Q 1295,325 1246,325 Q 1214,325 1189.5,340 Q 1165,355 1151.5,386.5 Q 1138,418 1138,467 Q 1138,540 1168,575.5 Q 1198,611 1246,611 Z M 1657,902 Q 1620,902 1600.5,882.5 Q 1581,863 1581,825 V 290 Q 1581,253 1600.5,233.5 Q 1620,214 1655,214 Q 1692,214 1711,233.5 Q 1730,253 1730,290 V 293 Q 1747,263 1780,241 Q 1825,212 1884,212 Q 1948,212 1996.5,243 Q 2045,274 2072,331 Q 2099,388 2099,467 Q 2099,546 2072,604 Q 2045,662 1997,693 Q 1949,724 1884,724 Q 1826,724 1781,695 Q 1749,675 1732,647 V 825 Q 1732,863 1712.5,882.5 Q 1693,902 1657,902 Z M 1839,611 Q 1871,611 1895.5,595 Q 1920,579 1933.5,547.5 Q 1947,516 1947,467 Q 1947,394 1917,359.5 Q 1887,325 1839,325 Q 1807,325 1782.5,340 Q 1758,355 1744,386.5 Q 1730,418 1730,467 Q 1730,540 1760,575.5 Q 1790,611 1839,611 Z M 2249,902 Q 2212,902 2192.5,882.5 Q 2173,863 2173,825 V 290 Q 2173,253 2192.5,233.5 Q 2212,214 2247,214 Q 2284,214 2303,233.5 Q 2322,253 2322,290 V 293 Q 2339,263 2372,241 Q 2417,212 2476,212 Q 2540,212 2588.5,243 Q 2637,274 2664,331 Q 2691,388 2691,467 Q 2691,546 2664,604 Q 2637,662 2589,693 Q 2541,724 2476,724 Q 2418,724 2373,695 Q 2341,675 2324,647 V 825 Q 2324,863 2304.5,882.5 Q 2285,902 2249,902 Z M 2431,611 Q 2463,611 2487.5,595 Q 2512,579 2525.5,547.5 Q 2539,516 2539,467 Q 2539,394 2509,359.5 Q 2479,325 2431,325 Q 2399,325 2374.5,340 Q 2350,355 2336,386.5 Q 2322,418 2322,467 Q 2322,540 2352,575.5 Q 2382,611 2431,611 Z M 3001,724 Q 2922,724 2863.5,693 Q 2805,662 2773,604 Q 2741,546 2741,467 Q 2741,408 2759,361 Q 2777,314 2811.5,280.5 Q 2846,247 2894,229.5 Q 2942,212 3001,212 Q 3080,212 3138.5,243 Q 3197,274 3229.5,331 Q 3262,388 3262,467 Q 3262,527 3243.5,574 Q 3225,621 3191,655 Q 3157,689 3108.5,706.5 Q 3060,724 3001,724 Z M 3001,611 Q 3034,611 3058,595 Q 3082,579 3096,547.5 Q 3110,516 3110,467 Q 3110,394 3080,359.5 Q 3050,325 3001,325 Q 2969,325 2944.5,340 Q 2920,355 2906.5,386.5 Q 2893,418 2893,467 Q 2893,540 2923,575.5 Q 2953,611 3001,611 Z";
    private const double WordmarkWidth = 3262, WordmarkHeight = 902, WordmarkCap = 713;

    private static Geometry? _symbol, _wordmark;
    private static Geometry SymbolGeometry => _symbol ??= Frozen(Geometry.Parse(SymbolData));
    private static Geometry WordmarkGeometry => _wordmark ??= Frozen(Geometry.Parse(WordmarkData));

    private static Geometry Frozen(Geometry g) { g.Freeze(); return g; }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The mark alone, size x size DIPs.</summary>
    public static FrameworkElement Symbol(double size, Brush color, double stroke = 2.25)
    {
        var path = new WpfPath
        {
            Data = SymbolGeometry, Stroke = color, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(path);
        return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform };
    }

    /// <summary>The app icon: white mark on a cobalt rounded square.</summary>
    public static FrameworkElement Tile(double size)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.225), Background = Brush(Cobalt),
            Child = new Border { Padding = new Thickness(size * 0.16), Child = Symbol(size * 0.68, Brushes.White) },
        };
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

    /// <summary>Symbol + wordmark side by side (light backgrounds: cobalt mark, navy word).</summary>
    public static FrameworkElement Lockup(double height, bool onDark = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = Symbol(height, onDark ? Brushes.White : Brush(Cobalt));
        var word = Wordmark(height * 0.6, onDark ? Brushes.White : Brush(Navy));
        // the mark's ink is centred in its box; centre the word's cap height on it
        word.Margin = new Thickness(height * 0.16, height * 0.2, 0, 0);
        mark.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(mark);
        row.Children.Add(word);
        return row;
    }
}
