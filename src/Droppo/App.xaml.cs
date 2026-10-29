using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Droppo.Core;
using Droppo.Shell;
using Droppo.Shell.Studio;
using Forms = System.Windows.Forms;

namespace Droppo;

/// <summary>
/// No main window. Lives in the tray, watches for Shift-drags, shows the wheel, runs actions.
/// </summary>
public partial class App : Application
{
    private const string ShowSettingsEvent = @"Local\Droppo.ShowSettings";
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;
    private Settings _settings = new();
    private FFmpeg? _ffmpeg;
    private Qpdf? _qpdf;
    private readonly IPdfRenderer _pdfRenderer = new WindowsPdfRenderer();
    private IOcrEngine? _ocr;
    private bool _ocrChecked;
    private OfficeConverter? _office;
    private AppPrompts? _prompts;
    private Tray? _tray;
    private WheelWindow? _wheel;
    private DragWatcher? _watcher;
    private Toast? _toast;
    private HomeWindow? _home;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Droppo.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // already running (opened again from the Start menu, say): ask it to show Settings
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSettingsEvent);
                show.Set();
            }
            catch (Exception)
            {
                MessageBox.Show("Droppo is already running. Look for its icon by the clock.", "Droppo");
            }
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => ShowHome())), null, Timeout.Infinite, executeOnlyOnce: false);

        DispatcherUnhandledException += OnUnhandled;
        Forms.Application.EnableVisualStyles();

        _settings = Settings.Load();
        AutoStart.MigrateOldEntry();
        Glass.UserEnabled = _settings.GlassEffects;
        Motion.UserEnabled = _settings.Animations;
        Theme.Load(this);
        _ffmpeg = FFmpeg.TryCreate(_settings.FfmpegPath);
        _qpdf = Qpdf.TryCreate(_settings.QpdfPath);
        Log.Write($"Started. ffmpeg = {_ffmpeg?.ExePath ?? "NOT FOUND"}, qpdf = {_qpdf?.ExePath ?? "NOT FOUND"}");

        // Droppo.exe --transcribe-test <folder>: transcribes every .wav/.mp4 in it with the real engine (GitHub build check)
        int listen = Array.FindIndex(e.Args, a => string.Equals(a, "--transcribe-test", StringComparison.OrdinalIgnoreCase));
        if (listen >= 0)
        {
            var folder = listen + 1 < e.Args.Length ? e.Args[listen + 1] : "transcribe-test";
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await SelfTest.TranscribeAsync(folder); }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "crash.txt"), ex.ToString());
                }
                finally { Shutdown(); }
            }));
            return;
        }

        // Droppo.exe --previews <folder>: draws every window into PNGs and quits (used by the GitHub build to check the look)
        int preview = Array.FindIndex(e.Args, a => string.Equals(a, "--previews", StringComparison.OrdinalIgnoreCase));
        if (preview >= 0)
        {
            var folder = preview + 1 < e.Args.Length ? e.Args[preview + 1] : "previews";
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await Previews.RunAsync(folder, _settings, CreateContext); }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder, "crash.txt"), ex.ToString());
                }
                finally { Shutdown(); }
            }));
            return;
        }

        _office = new OfficeConverter(() => _settings.LibreOfficePath);
        _prompts = new AppPrompts(OpenStudio);

        var tray = new Tray(_settings);
        tray.ExitRequested += () => Shutdown();
        tray.Say = (kind, title, body) => ShowToast(kind, title, body);
        tray.TestWheelRequested += ShowTestWheel;
        tray.StudioRequested += () => OpenStudio(Array.Empty<string>());
        tray.SettingsRequested += ShowSettings;
        tray.HomeRequested += () => ShowHome();
        _tray = tray;

        var watcher = new DragWatcher { TriggerKey = () => NativeMethods.VirtualKeyFor(_settings.TriggerKey) };
        var wheel = new WheelWindow(() => _settings);
        wheel.ActionChosen += OnActionChosen;
        wheel.Aborted += () => watcher.Suppress();
        _wheel = wheel;

        watcher.TriggerPressed += p => wheel.ShowAt(p);
        watcher.TriggerReleased += () => wheel.HideNow();
        watcher.DragEnded += () => wheel.HideSoon(350); // the drop itself arrives a moment later
        watcher.Start();
        _watcher = watcher;

        bool atStartup = e.Args.Any(a => string.Equals(a, AutoStart.StartupArg, StringComparison.OrdinalIgnoreCase));
        if (!_settings.WelcomeShown)
        {
            _settings.WelcomeShown = true;
            _settings.Save();
            ShowHome();
            WelcomeWindow.Show(_settings.TriggerKey, ShowTestWheel);
        }
        else if (_ffmpeg == null || _qpdf == null)
        {
            var missing = _ffmpeg == null ? "FFmpeg (pictures, video, audio)" : "qpdf (PDF tools)";
            ShowToast(ToastKind.Error, "Something's missing", $"{missing} wasn't found. Reinstalling Droppo puts it back.",
                extra: ("Settings", ShowSettings));
        }
        else if (!atStartup)
        {
            // opened by hand: show the main window. Starting with Windows stays quiet in the tray.
            ShowHome();
        }
    }

    /// <summary>The main window (one at a time); brings it forward if it's already open.</summary>
    private void ShowHome(IReadOnlyList<string>? files = null)
    {
        if (_home == null)
        {
            var home = new HomeWindow(() => _settings);
            home.ActionChosen += OnActionChosen;
            home.StudioRequested += () => OpenStudio(Array.Empty<string>());
            home.SettingsRequested += ShowSettings;
            home.Closed += (_, _) => _home = null;
            _home = home;
            home.Show();
        }
        if (_home.WindowState == WindowState.Minimized) _home.WindowState = WindowState.Normal;
        _home.Activate();
        if (files != null) _home.ShowFiles(files);
    }

    private SettingsWindow? _settingsWindow;

    private void ShowSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        var window = new SettingsWindow(_settings);
        window.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (!window.Saved) return;
            // tool paths may have changed: look them up again next time they're needed
            _ffmpeg = null;
            _qpdf = null;
            Glass.UserEnabled = _settings.GlassEffects;
            Motion.UserEnabled = _settings.Animations;
            Log.Write("Settings saved.");
        };
        _settingsWindow = window;
        window.Show();
    }

    private void ShowTestWheel()
    {
        var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1280, 720);
        _wheel?.ShowForTest(new NativeMethods.POINT(area.Left + area.Width / 2, area.Top + area.Height / 2));
    }

    /// <summary>Everything an action or Studio needs. Picks up tools installed after launch.</summary>
    private ActionContext CreateContext() => CreateContext(CancellationToken.None);

    private ActionContext CreateContext(CancellationToken token)
    {
        _ffmpeg ??= FFmpeg.TryCreate(_settings.FfmpegPath);
        _qpdf ??= Qpdf.TryCreate(_settings.QpdfPath);
        if (!_ocrChecked)
        {
            _ocr = WindowsOcrEngine.TryCreate();
            _ocrChecked = true;
        }
        return new ActionContext
        {
            FFmpeg = _ffmpeg, Qpdf = _qpdf, PdfRenderer = _pdfRenderer, OcrEngine = _ocr,
            Office = _office, Prompts = _prompts, Settings = _settings, Token = token,
        };
    }

    private void OpenStudio(IReadOnlyList<string> files)
    {
        _qpdf ??= Qpdf.TryCreate(_settings.QpdfPath);
        if (_qpdf == null)
        {
            ShowToast(ToastKind.Error, "PDF Studio can't open", "qpdf (the PDF engine) wasn't found. Reinstalling Droppo puts it back.");
            return;
        }
        var window = new StudioWindow(files, CreateContext, (title, text, reveal) => ShowToast(ToastKind.Done, title, text, reveal, reveal));
        window.Show();
        window.Activate();
    }

    private async void OnActionChosen(WheelAction action, IReadOnlyList<string> files)
    {
        _watcher?.Suppress();
        _tray?.SetBusy(true);
        DispatcherTimer? slow = null;
        using var stop = new CancellationTokenSource();
        (double Fraction, string? Text)? latest = null;
        try
        {
            var ctx = CreateContext(stop.Token);
            if (!action.Silent && action.ClipboardText == null)
            {
                string working = $"{action.Label} \u00B7 {FileKinds.Describe(files)}";
                // only say "working" when it takes a moment; quick jobs go straight to "All done."
                ctx.WorkStarted = () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    slow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
                    slow.Tick += (_, _) =>
                    {
                        slow?.Stop();
                        if (stop.IsCancellationRequested) return;
                        ShowToast(ToastKind.Working, "Working on it\u2026", latest?.Text ?? working,
                            extra: ("Stop", () => { try { stop.Cancel(); } catch (ObjectDisposedException) { } }));
                        if (latest is { } l) _toast?.SetProgress(l.Fraction, l.Text);
                    };
                    slow.Start();
                }));
                ctx.ProgressChanged = (fraction, text) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    var line = text == null ? working : files.Count > 1 ? $"{text} ({Math.Min(files.Count, (int)(fraction * files.Count) + 1)} of {files.Count})" : text;
                    latest = (fraction, line);
                    if (_toast?.IsWorking == true && !stop.IsCancellationRequested) _toast.SetProgress(fraction, line);
                }));
            }
            var result = await Task.Run(() => ActionRunner.RunAsync(action, files, ctx));
            slow?.Stop();
            if (stop.IsCancellationRequested)
            {
                ShowToast(ToastKind.Info, "Stopped.", result.Outputs.Count > 0 ? $"{result.Outputs.Count} saved before stopping." : "Nothing was saved.",
                          reveal: result.Outputs.FirstOrDefault());
                return;
            }
            Report(action, files, result);
        }
        catch (Exception ex)
        {
            slow?.Stop();
            Log.Write("Action crashed: " + ex);
            ShowToast(ToastKind.Error, Couldnt(files.Count), ex.Message, extra: ("Details", OpenLog));
        }
        finally
        {
            _tray?.SetBusy(false);
        }
    }

    private static string Couldnt(int count) => count == 1 ? "Couldn't process this file." : "Couldn't process these files.";

    /// <summary>Tells the person what happened, in a few words. Never says done unless it is.</summary>
    private void Report(WheelAction action, IReadOnlyList<string> files, ActionResult result)
    {
        if (result.Cancelled || (action.Silent && result.Ok && result.Outputs.Count == 0 && result.Notes.Count == 0))
        {
            _toast?.Dismiss(); // they closed a question, or a window opened
            return;
        }

        if (result.ClipboardText != null)
        {
            try
            {
                Clipboard.SetText(result.ClipboardText);
                var said = result.Notes.Count > 0 ? string.Join("\n", result.Notes) : result.ClipboardText;
                ShowToast(ToastKind.Copied, "Copied.", said, reveal: result.Outputs.FirstOrDefault());
            }
            catch (Exception ex)
            {
                ShowToast(ToastKind.Error, "Couldn't copy that.", "The clipboard was busy. Try again. " + ex.Message);
            }
            return;
        }

        var notes = string.Join("\n", result.Notes);
        var first = result.Outputs.FirstOrDefault();

        if (!result.Ok)
        {
            int failed = result.Errors.Count;
            string title = files.Count <= 1 || failed >= files.Count ? Couldnt(files.Count) : $"Couldn't process {failed} of {files.Count} files.";
            string body = Plain(result.Errors[0]) + (failed > 1 ? $" (+{failed - 1} more)" : "");
            if (result.Outputs.Count > 0) body += $"\n{result.Outputs.Count} saved.";
            ShowToast(ToastKind.Error, title, body, reveal: first, extra: ("Details", OpenLog));
            return;
        }

        if (result.Outputs.Count == 0)
        {
            ShowToast(ToastKind.Info, action.Label, string.IsNullOrEmpty(notes) ? "Nothing to do here." : notes);
            return;
        }

        string saved = result.Outputs.Count == 1 ? $"Your file is ready: {Path.GetFileName(first!.TrimEnd('\\', '/'))}" : $"{result.Outputs.Count} files are ready.";
        ShowToast(ToastKind.Done, "All done!", string.IsNullOrEmpty(notes) ? saved : saved + "\n" + notes,
                  reveal: first, open: result.Outputs.Count == 1 && File.Exists(first) ? first : null);
    }

    /// <summary>"photo.jpg: something broke" reads better without the file name when there's only one file.</summary>
    private static string Plain(string error)
    {
        int colon = error.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon < 80 ? error[(colon + 2)..] : error;
    }

    private void ShowToast(ToastKind kind, string title, string body, string? reveal = null, string? open = null, (string Label, Action Run)? extra = null)
    {
        if (Toast.CanShowNow())
        {
            (_toast ??= new Toast()).Show(kind, title, body, reveal, open, extra);
            return;
        }
        // a game or presentation is full-screen: let Windows decide when to show it
        if (kind != ToastKind.Working) _tray?.Notify(title, body, error: kind == ToastKind.Error, reveal: reveal);
    }

    private static void OpenLog()
    {
        try { System.Diagnostics.Process.Start("notepad.exe", $"\"{Log.FilePath}\""); }
        catch (Exception ex) { Log.Write("Couldn't open the log: " + ex.Message); }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        ShowToast(ToastKind.Error, "Something went wrong.", e.Exception.Message, extra: ("Details", OpenLog));
        e.Handled = true; // keep running, a converter crashing the tray app would be annoying
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _watcher?.Stop();
        _toast?.Close();
        _tray?.Dispose();
        _wheel?.Close();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
