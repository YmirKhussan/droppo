using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Droppo.Core;
using Droppo.Shell.Studio;

namespace Droppo.Shell;

/// <summary>
/// "Droppo.exe --previews folder": opens every window and dialog, saves a picture of each, closes it, and quits.
/// The GitHub build runs this on Windows so the real look can be checked without a person clicking through.
/// Each window gets two pictures: "-render" (what WPF drew) and "-screen" (what was actually on screen).
/// </summary>
internal static class Previews
{
    private static string _out = "";
    private static int _count;
    private static readonly StringBuilder Notes = new();

    public static async Task RunAsync(string folder, Settings settings, Func<ActionContext> context)
    {
        _out = Path.GetFullPath(folder);
        Directory.CreateDirectory(_out);
        Motion.UserEnabled = false; // pictures of the finished state, not mid-animation
        Notes.AppendLine($"Glass enabled: {Glass.Enabled}");
        Notes.AppendLine($"Theme parts loaded: {Application.Current.Resources.MergedDictionaries.Count} of 8");

        // dialogs: picture as soon as they've drawn, then close them so the next one can open
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window w && !Equals(w.Tag, "keep") && w is not Toast && w is not WheelWindow)
                After(500, () => { Snap(w, w.Title); w.Close(); });
        }));

        // a colourful stand-in desktop, so the glass on the wheel and notifications has something to blur
        var desktop = new Window
        {
            Tag = "keep", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Left = 0, Top = 0,
            Width = SystemParameters.PrimaryScreenWidth, Height = SystemParameters.PrimaryScreenHeight, ShowInTaskbar = false,
            Background = new LinearGradientBrush(Color.FromRgb(0xF0, 0x8A, 0x5D), Color.FromRgb(0x3A, 0x7B, 0xD5), 30),
            Content = new TextBlock
            {
                Text = "Holiday photos\nReceipts\nReport.pdf", FontSize = 40, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Margin = new Thickness(60), VerticalAlignment = VerticalAlignment.Center,
            },
        };
        desktop.Show();
        await Delay(800);

        // ---- the wheel ----
        var center = new NativeMethods.POINT((int)(SystemParameters.PrimaryScreenWidth / 2 * Scale()), (int)(SystemParameters.PrimaryScreenHeight / 2 * Scale()));
        var wheel = new WheelWindow(() => settings);
        var photo = new[] { Path.Combine(_out, "holiday.jpg") };
        wheel.ShowArmedForPreview(center, photo, ActionRegistry.For(photo, settings), 5);
        await Delay(700);
        Notes.AppendLine($"Wheel glass picture: {(wheel.BehindLayer.Children.Count > 0 ? "yes" : "no")}");
        Snap(wheel, "wheel");
        wheel.HideNow();
        await Delay(300);
        wheel.ShowForTest(center);
        await Delay(600);
        Snap(wheel, "wheel test");
        wheel.HideNow();
        await Delay(300);

        // ---- notifications ----
        var toast = new Toast();
        toast.Show(ToastKind.Working, "Working on it…", "Smaller · 1 video");
        await Delay(700);
        Snap(toast, "toast working");
        var made = Path.Combine(_out, "holiday-1080x1080.jpg");
        File.WriteAllText(made, "x");
        toast.Show(ToastKind.Done, "All done!", "Your file is ready: holiday-1080x1080.jpg", reveal: made, open: made);
        await Delay(500);
        Snap(toast, "toast done");
        toast.Dismiss();
        await Delay(300);
        toast.Show(ToastKind.Error, "Couldn't process this file.", "The background isn't one plain colour, so it can't be removed cleanly.", extra: ("Details", () => { }));
        await Delay(700);
        Snap(toast, "toast error");
        toast.Dismiss();

        // ---- dialogs (each closes itself after its picture) ----
        Dialogs.AskTargetSize(290_440_000, 1, new SizeTarget(10, false), null);
        Dialogs.AskTargetSize(45_000_000, 3, new SizeTarget(50, true), null);
        Dialogs.AskPassword("Password needed", "\"report.pdf\" is password-protected. Enter its password:", false, null);
        Dialogs.AskPassword("Protect with a password", "Anyone opening the saved PDF will need this password.", true, null);
        Dialogs.AskResize(1, (4032, 3024), null);
        var files = SampleFiles();
        Dialogs.AskRename(files, null);
        Dialogs.AskOrder("Join clips", "Set the order the clips play in.", files, null);
        Dialogs.AskTrim(Path.Combine(_out, "clip.mp4"), 83.5, true, _ => Task.FromResult<string?>(null), null);
        Dialogs.DrawSignature(null);
        Dialogs.PageNumbers(null, desktop);
        Dialogs.Watermark(null, desktop);

        // ---- windows ----
        var home = new HomeWindow(() => settings) { Tag = "keep" };
        home.Show();
        await Delay(900);
        Snap(home, "home");
        var picks = new[] { Path.Combine(_out, "holiday.jpg") };
        File.WriteAllText(picks[0], "x");
        home.ShowFiles(picks);
        await Delay(700);
        Snap(home, "home with a photo");
        home.Close();
        await ShowAndWait(MakeWelcome(settings));
        await ShowAndWait(new SettingsWindow(settings));
        await ShowAndWait(new StudioWindow(Array.Empty<string>(), context, (_, _, _) => { }));

        desktop.Close();
        Notes.AppendLine($"Pictures: {_count}");
        File.WriteAllText(Path.Combine(_out, "notes.txt"), Notes.ToString());
        try { if (File.Exists(Log.FilePath)) File.Copy(Log.FilePath, Path.Combine(_out, "log.txt"), true); } catch { }
    }

    private static Window MakeWelcome(Settings settings)
    {
        Window? created;
        var before = Application.Current.Windows.Cast<Window>().ToList();
        WelcomeWindow.Show(settings.TriggerKey, () => { });
        created = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => !before.Contains(w));
        return created ?? new Window();
    }

    private static Task ShowAndWait(Window w)
    {
        var done = new TaskCompletionSource();
        w.Closed += (_, _) => done.TrySetResult();
        if (!w.IsVisible) w.Show();
        // safety net: never hang the build
        After(15000, () => { if (w.IsVisible) w.Close(); });
        return done.Task;
    }

    private static List<string> SampleFiles()
    {
        var dir = Path.Combine(_out, "samples");
        Directory.CreateDirectory(dir);
        var list = new List<string>();
        foreach (var name in new[] { "IMG_2041.jpg", "IMG_2042.jpg", "IMG_2050.jpg", "scan 1.pdf" })
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "x");
            list.Add(path);
        }
        return list;
    }

    /// <summary>Saves what WPF drew for the window, and what's on the screen where it is.</summary>
    private static void Snap(Window w, string name)
    {
        string slug = $"{++_count:00}-" + new string((name ?? "window").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        try
        {
            w.UpdateLayout();
            if (VisualTreeHelper.GetChildrenCount(w) > 0 && VisualTreeHelper.GetChild(w, 0) is FrameworkElement root && root.ActualWidth > 0)
            {
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * 2), (int)Math.Ceiling(root.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                bitmap.Render(root);
                Save(bitmap, Path.Combine(_out, slug + "-render.png"));
            }
        }
        catch (Exception ex)
        {
            Notes.AppendLine($"{slug}: render failed: {ex.Message}");
        }
        try
        {
            double s = Scale();
            int x = (int)Math.Round(w.Left * s), y = (int)Math.Round(w.Top * s);
            int width = (int)Math.Round(w.ActualWidth * s), height = (int)Math.Round(w.ActualHeight * s);
            if (width > 0 && height > 0)
            {
                using var shot = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(shot)) g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(width, height));
                shot.Save(Path.Combine(_out, slug + "-screen.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        catch (Exception ex)
        {
            Notes.AppendLine($"{slug}: screen capture failed: {ex.Message}");
        }
        Notes.AppendLine($"{slug}: {w.GetType().Name} {w.ActualWidth:0}x{w.ActualHeight:0}");
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double Scale()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        return screen == null || SystemParameters.PrimaryScreenWidth <= 0 ? 1 : screen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
    }

    private static Task Delay(int ms)
    {
        var done = new TaskCompletionSource();
        After(ms, () => done.TrySetResult());
        return done.Task;
    }

    private static void After(int ms, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }
}
