using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Droppo.Core;
using Microsoft.Win32;

namespace Droppo.Shell;

/// <summary>
/// Droppo's glass: frosted, see-through panels with bright edges over a soft cobalt backdrop.
/// Windows get the brand backdrop; the wheel and notifications blur whatever is on your screen behind them.
/// Everything is drawn by the app itself, so it looks the same on Windows 10 and 11.
/// Off (Settings, Windows "Transparency effects" off, or high contrast) = plain cloud white and white cards.
/// </summary>
internal static class Glass
{
    /// <summary>Settings > Look and feel.</summary>
    public static bool UserEnabled { get; set; } = true;

    public static bool Enabled => UserEnabled && TransparencyOn() && !SystemParameters.HighContrast;

    private static bool TransparencyOn()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    // ---------- brushes ----------

    private static readonly Brush Flat = Brand.Brush(Brand.Cloud);
    private static ImageBrush? _backdrop;
    private static bool _backdropTried;

    /// <summary>The soft cobalt backdrop behind every Droppo window (cloud white when glass is off).</summary>
    public static Brush WindowBackground
    {
        get
        {
            if (!Enabled) return Flat;
            if (!_backdropTried)
            {
                _backdropTried = true;
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri("pack://application:,,,/Assets/backdrop.jpg");
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                    brush.Freeze();
                    _backdrop = brush;
                }
                catch (Exception ex)
                {
                    Log.Write("Backdrop image didn't load: " + ex.Message);
                }
            }
            return (Brush?)_backdrop ?? Flat;
        }
    }

    // see-through panes: the blurred desktop shows through, a bright edge catches the light
    private static readonly Brush CardFill = Gradient(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardEdge = Gradient(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
    private static readonly Brush BarFill = Gradient(Color.FromArgb(0x47, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    // the window's own frost over the blurred desktop: enough white that navy text always reads
    private static readonly Brush Tint = Gradient(Color.FromArgb(0xA6, 0xF8, 0xFB, 0xFF), Color.FromArgb(0x8C, 0xE8, 0xF1, 0xFF));
    private static readonly Brush SolidWhite = Brand.Brush(Colors.White);
    private static readonly Brush FlatEdge = Brand.Brush(Color.FromArgb(0x24, 0x0F, 0x2D, 0x5B));

    private static Brush Gradient(Color topLeft, Color bottomRight)
    {
        var b = new LinearGradientBrush(topLeft, bottomRight, new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    public static Brush CardBackground => Enabled ? CardFill : SolidWhite;

    /// <summary>Frost for a window over the blurred screen: more white over dark screens so text stays readable.</summary>
    public static Brush FrostFor(double brightness)
    {
        byte top = brightness < 0.3 ? (byte)0xC7 : brightness < 0.55 ? (byte)0xA6 : (byte)0x8C;
        byte bottom = (byte)Math.Max(0, top - 0x20);
        return Gradient(Color.FromArgb(top, 0xF8, 0xFB, 0xFF), Color.FromArgb(bottom, 0xEC, 0xF3, 0xFF));
    }

    /// <summary>The see-through frost laid over the blurred desktop behind a window.</summary>
    public static Brush WindowTint => Enabled ? Tint : Flat;
    public static Brush CardBorder => Enabled ? CardEdge : FlatEdge;
    public static Brush BarBackground => Enabled ? BarFill : SolidWhite;

    /// <summary>A frosted glass card around <paramref name="child"/>, lifted off the page by a soft shadow.</summary>
    public static System.Windows.Controls.Border Card(UIElement child, Thickness? padding = null, double radius = 14, bool shadow = false) => new()
    {
        Child = child, Background = CardBackground, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(18, 16, 18, 16), SnapsToDevicePixels = true,
        Effect = shadow || !Enabled ? SoftShadow() : null, // see-through glass casts no muddy shadow
    };

    /// <summary>The board's soft, wide, barely-there shadow.</summary>
    public static System.Windows.Media.Effects.DropShadowEffect SoftShadow(double opacity = 0.08) => new()
    {
        BlurRadius = 26, ShadowDepth = 5, Direction = 270, Opacity = opacity, Color = Brand.Navy, RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
    };

    // ---------- real blur of the desktop behind small windows ----------

    /// <summary>
    /// Copies the screen area (physical pixels) and shrinks it a lot: shown stretched back up, it is a soft blur
    /// of whatever was behind. Call before the window is visible, or it captures itself. Null if it fails.
    /// </summary>
    public static (BitmapSource Image, double Brightness)? CaptureBehind(int x, int y, int width, int height)
    {
        if (!Enabled || width <= 0 || height <= 0) return null;
        try
        {
            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
            IntPtr hbitmap = bitmap.GetHbitmap();
            try
            {
                int sw = Math.Max(4, width / 10), sh = Math.Max(4, height / 10);
                var small = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(sw, sh));
                var converted = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
                var pixels = new byte[sw * sh * 4];
                converted.CopyPixels(pixels, sw * 4, 0);
                double sum = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    sum += 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
                    pixels[i + 3] = 255; // the screen is opaque
                }
                var frozen = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, pixels, sw * 4);
                frozen.Freeze();
                return (frozen, sum / (sw * sh) / 255.0);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't read the screen for the glass effect: " + ex.Message);
            return null;
        }
    }

    /// <summary>The captured screen, stretched smoothly so it reads as frosted glass.</summary>
    public static System.Windows.Controls.Image BlurredImage(BitmapSource source)
    {
        var image = new System.Windows.Controls.Image { Source = source, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        image.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 14, KernelType = System.Windows.Media.Effects.KernelType.Gaussian };
        image.CacheMode = new BitmapCache(); // the blur is worked out once, not on every frame
        return image;
    }

    /// <summary>How much white to lay over the blurred desktop so navy text stays readable (more on dark desktops).</summary>
    public static double TintFor(double brightness) => brightness < 0.3 ? 0.8 : brightness < 0.55 ? 0.72 : 0.64;

    // ---------- real glass: Windows blurs what's behind the window ----------

    /// <summary>
    /// Asks Windows to blur whatever is behind this window (the same effect as the Start menu on Windows 10).
    /// The window's own background then only needs to be a light, see-through tint. False if unavailable.
    /// </summary>
    public static bool EnableBlurBehind(IntPtr hwnd)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Blur behind unavailable: " + ex.Message);
            return false;
        }
    }

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState, AccentFlags, GradientColor, AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    // ---------- window corners ----------

    /// <summary>Round corners on Windows 11 (ignored on Windows 10).</summary>
    public static void RoundCorners(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Write("Rounded corners unavailable: " + ex.Message);
            }
        };
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}

/// <summary>
/// Short, purposeful animations. Every one is under 200 ms and runs on the render thread;
/// all of them switch off with Windows' "Animation effects" setting or Settings > Look.
/// </summary>
internal static class Motion
{
    public static bool UserEnabled { get; set; } = true;

    public static bool Enabled => UserEnabled && SystemParameters.ClientAreaAnimation && (RenderCapability.Tier >> 16) > 0;

    public static readonly IEasingFunction Out = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }

    /// <summary>Sets the final value straight away, then animates towards it (so nothing is left "held" by an animation).</summary>
    public static void To(IAnimatable target, DependencyProperty property, double from, double to, int ms)
    {
        if (target is DependencyObject d) d.SetValue(property, to);
        if (!Enabled) { target.BeginAnimation(property, null); return; }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop };
        target.BeginAnimation(property, animation);
    }

    /// <summary>Fade in while rising a few pixels. For panels and dialogs as they appear.</summary>
    public static void Rise(UIElement element, double fromY = 8, int ms = 160)
    {
        if (!Enabled) return;
        var shift = new TranslateTransform(0, 0);
        element.RenderTransform = shift;
        To(element, UIElement.OpacityProperty, 0, 1, ms);
        To(shift, TranslateTransform.YProperty, fromY, 0, ms);
    }

    public static void FadeIn(UIElement element, int ms = 120) => To(element, UIElement.OpacityProperty, 0, element.Opacity > 0 ? element.Opacity : 1, ms);

    /// <summary>Animates a brush's colour (the brush must not be frozen).</summary>
    public static void Color(SolidColorBrush brush, Color to, int ms)
    {
        if (!Enabled) { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = to; return; }
        var from = brush.Color;
        brush.Color = to;
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop });
    }
}
