using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Droppo.Core;
using Microsoft.Win32;

namespace Droppo.Shell;

/// <summary>
/// Real frosted glass (Mica / Acrylic) behind windows on Windows 11. The blur is done by Windows itself,
/// so it costs the app nothing. Falls back to plain colours on Windows 10, with "Transparency effects"
/// off, in high contrast, or when switched off in Settings.
/// </summary>
internal static class Glass
{
    /// <summary>Settings > Look: glass effects.</summary>
    public static bool UserEnabled { get; set; } = true;

    private static readonly bool Supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621); // Windows 11 22H2

    public static bool Enabled => UserEnabled && Supported && TransparencyOn() && !SystemParameters.HighContrast;

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

    /// <summary>
    /// Call before the window is shown. Mica for main windows, Acrylic for small dialogs.
    /// The window keeps its normal background if glass isn't available or fails.
    /// </summary>
    public static void Apply(Window window, bool acrylic = false)
    {
        if (!Enabled) return;
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int light = 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref light, sizeof(int));
                int round = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                if (DwmExtendFrameIntoClientArea(hwnd, ref margins) != 0) return;
                int backdrop = acrylic ? DWMSBT_TRANSIENTWINDOW : DWMSBT_MAINWINDOW;
                if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0) return;

                if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                {
                    target.BackgroundColor = Colors.Transparent;
                    window.Background = Brushes.Transparent;
                }
            }
            catch (Exception ex)
            {
                Log.Write("Glass effect unavailable: " + ex.Message);
            }
        };
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
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
