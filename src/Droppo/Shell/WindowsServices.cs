using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Droppo.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Droppo.Shell;

/// <summary>Text recognition built into Windows 10/11. Uses the languages set up in Windows settings.</summary>
internal sealed class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine _engine;

    private WindowsOcrEngine(OcrEngine engine) => _engine = engine;

    public static WindowsOcrEngine? TryCreate()
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            return engine == null ? null : new WindowsOcrEngine(engine);
        }
        catch (Exception ex)
        {
            Log.Write("Windows OCR unavailable: " + ex.Message);
            return null;
        }
    }

    public async Task<IReadOnlyList<Droppo.Core.OcrLine>> RecognizeAsync(string imagePath, CancellationToken ct)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(imagePath));
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // Windows OCR has a maximum image size; shrink bigger pages and scale the boxes back up
        double max = OcrEngine.MaxImageDimension;
        double scale = Math.Min(1.0, max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        ct.ThrowIfCancellationRequested();

        var result = await _engine.RecognizeAsync(bitmap);
        var lines = new List<Droppo.Core.OcrLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Select(w => new Droppo.Core.OcrWord(
                w.Text, w.BoundingRect.X / scale, w.BoundingRect.Y / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale)).ToList();
            if (words.Count > 0) lines.Add(new Droppo.Core.OcrLine(words));
        }
        return lines;
    }
}

/// <summary>
/// Word/PowerPoint/Excel to PDF. Uses Microsoft Office when it's installed (best fidelity),
/// otherwise LibreOffice.
/// </summary>
internal sealed class OfficeConverter : IOfficeConverter
{
    private readonly Func<string?> _libreOfficePath;

    public OfficeConverter(Func<string?> libreOfficePath) => _libreOfficePath = libreOfficePath;

    public bool IsAvailable => HasOffice(".docx") || HasOffice(".pptx") || HasOffice(".xlsx") || LibreOfficeConverter.TryCreate(_libreOfficePath()) != null;

    public async Task ConvertToPdfAsync(string input, string outputPdf, CancellationToken ct)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        Exception? officeError = null;
        if (HasOffice(ext))
        {
            try
            {
                await RunOnStaThread(() => ConvertWithOffice(Path.GetFullPath(input), Path.GetFullPath(outputPdf), ext));
                if (File.Exists(outputPdf)) return;
            }
            catch (Exception ex)
            {
                officeError = ex;
                Log.Write($"Microsoft Office couldn't convert {input}: {ex}");
            }
        }

        var libre = LibreOfficeConverter.TryCreate(_libreOfficePath());
        if (libre != null)
        {
            await libre.ConvertToPdfAsync(input, outputPdf, ct);
            return;
        }
        if (officeError != null)
            throw new ToolException("Microsoft Office couldn't convert this file: " + officeError.Message);
        throw new ToolMissingException("Word, PowerPoint and Excel to PDF needs Microsoft Office or LibreOffice. LibreOffice is free from libreoffice.org.");
    }

    private static string? ProgIdFor(string ext) => ext switch
    {
        ".doc" or ".docx" or ".rtf" or ".odt" => "Word.Application",
        ".ppt" or ".pptx" or ".odp" => "PowerPoint.Application",
        ".xls" or ".xlsx" or ".ods" => "Excel.Application",
        _ => null,
    };

    private static bool HasOffice(string ext) => ProgIdFor(ext) is { } id && Type.GetTypeFromProgID(id) != null;

    /// <summary>Drives the installed Office app invisibly through COM and exports a PDF.</summary>
    private static void ConvertWithOffice(string input, string output, string ext)
    {
        var type = Type.GetTypeFromProgID(ProgIdFor(ext)!)!;
        dynamic app = Activator.CreateInstance(type)!;
        try
        {
            switch (ProgIdFor(ext))
            {
                case "Word.Application":
                {
                    app.Visible = false;
                    app.DisplayAlerts = 0;
                    dynamic doc = app.Documents.Open(input, false, true, false); // no conversion prompt, read-only, not in recent files
                    try { doc.ExportAsFixedFormat(output, 17); } // 17 = PDF
                    finally { doc.Close(false); Marshal.FinalReleaseComObject(doc); }
                    break;
                }
                case "PowerPoint.Application":
                {
                    dynamic pres = app.Presentations.Open(input, -1, 0, 0); // read-only, keep title, no window
                    try { pres.SaveAs(output, 32); } // 32 = PDF
                    finally { pres.Close(); Marshal.FinalReleaseComObject(pres); }
                    break;
                }
                default:
                {
                    app.Visible = false;
                    app.DisplayAlerts = false;
                    dynamic book = app.Workbooks.Open(input, 0, true); // don't update links, read-only
                    try { book.ExportAsFixedFormat(0, output); } // 0 = PDF
                    finally { book.Close(false); Marshal.FinalReleaseComObject(book); }
                    break;
                }
            }
        }
        finally
        {
            try { app.Quit(); } catch { }
            Marshal.FinalReleaseComObject(app);
        }
    }

    private static Task RunOnStaThread(Action action)
    {
        var done = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); // Office's COM objects expect this
        thread.IsBackground = true;
        thread.Start();
        return done.Task;
    }
}

/// <summary>Shows dialogs and windows on the UI thread when an action (running in the background) needs them.</summary>
internal sealed class AppPrompts : IUserPrompts
{
    private readonly Action<IReadOnlyList<string>> _openStudio;

    public AppPrompts(Action<IReadOnlyList<string>> openStudio) => _openStudio = openStudio;

    public Task<string?> AskPasswordAsync(string title, string message, bool confirm)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() => Studio.Dialogs.AskPassword(title, message, confirm, owner: null)).Task;
    }

    public Task<SizeTarget?> AskTargetSizeAsync(long largestFileBytes, int fileCount, SizeTarget last)
    {
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        return dispatcher.InvokeAsync(() => Studio.Dialogs.AskTargetSize(largestFileBytes, fileCount, last, owner: null)).Task;
    }

    public Task<TrimRange?> AskTrimAsync(string file, double durationSeconds, bool hasVideo, Func<double, Task<string?>> previewFrame) =>
        OnUi(() => Studio.Dialogs.AskTrim(file, durationSeconds, hasVideo, previewFrame, owner: null));

    public Task<IReadOnlyList<string>?> AskOrderAsync(string title, string message, IReadOnlyList<string> files) =>
        OnUi(() => Studio.Dialogs.AskOrder(title, message, files, owner: null));

    public Task<ResizeSpec?> AskResizeAsync(int fileCount, (int W, int H)? firstSize) =>
        OnUi(() => Studio.Dialogs.AskResize(fileCount, firstSize, owner: null));

    public Task<RenameRequest?> AskRenameAsync(IReadOnlyList<string> files) =>
        OnUi(() => Studio.Dialogs.AskRename(files, owner: null));

    public Task<string?> AskFolderAsync(string title) => OnUi(() =>
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    });

    public Task<string?> AskChoiceAsync(ChoiceRequest request) =>
        OnUi(() => Studio.Dialogs.AskChoice(request, owner: null));

    public void OpenStudio(IReadOnlyList<string> files)
    {
        var list = files.ToList();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => _openStudio(list)));
    }

    private static Task<T> OnUi<T>(Func<T> show) => System.Windows.Application.Current.Dispatcher.InvokeAsync(show).Task;
}
