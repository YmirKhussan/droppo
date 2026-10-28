using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>
/// qpdf: free, Apache-licensed PDF tool. Merges, splits, rotates and repacks PDFs without
/// touching their content, so text stays selectable and quality stays identical.
/// </summary>
public sealed class Qpdf
{
    public string ExePath { get; }

    public Qpdf(string exePath) => ExePath = exePath;

    public static Qpdf? TryCreate(string? configuredPath)
    {
        var path = Locate(configuredPath);
        return path == null ? null : new Qpdf(path);
    }

    public static string? Locate(string? configuredPath)
    {
        var extra = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            // the qpdf installer uses folders like "C:\Program Files\qpdf 12.2.0\bin"
            extra.AddRange(ToolLocator.SearchUnder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qpdf*", "qpdf.exe"));
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            extra.AddRange(ToolLocator.SearchUnder(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "*qpdf*", "qpdf.exe"));
            extra.AddRange(ToolLocator.SearchUnder(Path.Combine(local, "Programs"), "qpdf*", "qpdf.exe"));
        }
        return ToolLocator.Locate("qpdf", configuredPath, extra);
    }

    /// <summary>Exit code 3 means "done, with warnings" (common with slightly broken PDFs), which is fine.</summary>
    public Task<string> RunAsync(IEnumerable<string> args, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(ExePath, args, ct, isSuccess: code => code == 0 || code == 3, friendly: Friendly);

    /// <summary>
    /// Long page lists and passwords go through an argument file (one argument per line):
    /// it avoids Windows' command-length limit and keeps passwords out of logs and process lists.
    /// </summary>
    public async Task<string> RunWithArgFileAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var file = Path.Combine(tmp, "args.txt");
            await File.WriteAllLinesAsync(file, args, ct).ConfigureAwait(false);
            return await RunAsync(new[] { "@" + file }, ct).ConfigureAwait(false);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    public async Task<int> PageCountAsync(string pdf, CancellationToken ct = default)
    {
        var text = await RunAsync(new[] { "--show-npages", pdf }, ct).ConfigureAwait(false);
        return int.TryParse(text.Trim(), out var n) ? n : 0;
    }

    /// <summary>
    /// Per page: does it already use fonts (i.e. has real text)? Scanned pages don't. Used to avoid
    /// adding a second, OCR'd copy of text that's already searchable.
    /// </summary>
    public async Task<bool[]> PagesWithFontsAsync(string pdf, CancellationToken ct = default)
    {
        var json = await RunAsync(new[] { "--json=2", "--json-key=pages", "--json-key=qpdf", pdf }, ct).ConfigureAwait(false);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var objects = root.GetProperty("qpdf")[1];

        System.Text.Json.JsonElement? Resolve(System.Text.Json.JsonElement e)
        {
            if (e.ValueKind == System.Text.Json.JsonValueKind.String && e.GetString() is { } s && s.EndsWith(" R"))
                return objects.TryGetProperty("obj:" + s, out var o) && o.TryGetProperty("value", out var v) ? v : null;
            return e;
        }

        bool HasFonts(System.Text.Json.JsonElement page, int depth)
        {
            if (depth > 30 || page.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
            if (page.TryGetProperty("/Resources", out var resRaw) && Resolve(resRaw) is { } res && res.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (!res.TryGetProperty("/Font", out var fontRaw)) return false;
                return Resolve(fontRaw) is { } fonts && fonts.ValueKind == System.Text.Json.JsonValueKind.Object && fonts.EnumerateObject().Any();
            }
            return page.TryGetProperty("/Parent", out var parent) && Resolve(parent) is { } p && HasFonts(p, depth + 1);
        }

        var pages = root.GetProperty("pages");
        var result = new bool[pages.GetArrayLength()];
        int i = 0;
        foreach (var page in pages.EnumerateArray())
        {
            var obj = Resolve(page.GetProperty("object"));
            result[i++] = obj.HasValue && HasFonts(obj.Value, 0);
        }
        return result;
    }

    public async Task<bool> IsEncryptedAsync(string pdf, CancellationToken ct = default) =>
        await ProcessRunner.ExitCodeAsync(ExePath, new[] { "--is-encrypted", pdf }, ct).ConfigureAwait(false) == 0;

    /// <summary>True only when a password is needed to open the file (not just print/copy restrictions).</summary>
    public async Task<bool> RequiresPasswordAsync(string pdf, CancellationToken ct = default) =>
        await ProcessRunner.ExitCodeAsync(ExePath, new[] { "--requires-password", pdf }, ct).ConfigureAwait(false) == 0;

    /// <summary>Writes an unlocked copy. Returns false if the password is wrong.</summary>
    public async Task<bool> TryDecryptAsync(string pdf, string password, string output, CancellationToken ct = default)
    {
        try
        {
            await RunWithArgFileAsync(new[] { "--password=" + password, "--decrypt", pdf, output }, ct).ConfigureAwait(false);
            return true;
        }
        catch (ToolException ex) when (ex.Message == WrongPassword)
        {
            return false;
        }
    }

    public Task EncryptAsync(string pdf, string password, string output, CancellationToken ct = default) =>
        RunWithArgFileAsync(new[] { pdf, "--encrypt", password, password, "256", "--", output }, ct);

    private const string WrongPassword = "Wrong password, or this PDF is password-protected.";

    private static string? Friendly(string err)
    {
        if (err.Contains("invalid password", StringComparison.OrdinalIgnoreCase))
            return WrongPassword;
        if (err.Contains("not a PDF file", StringComparison.OrdinalIgnoreCase) || err.Contains("can't find PDF header", StringComparison.OrdinalIgnoreCase))
            return "This doesn't look like a valid PDF.";
        if (err.Contains("damaged file", StringComparison.OrdinalIgnoreCase) || err.Contains("trailer dictionary", StringComparison.OrdinalIgnoreCase))
            return "This PDF is damaged and couldn't be repaired.";
        if (err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Can't write to this folder (permission denied).";
        return null;
    }
}

/// <summary>Turns PDF pages into PNG images. On Windows this is the built-in renderer Edge uses.</summary>
public interface IPdfRenderer
{
    /// <summary>Renders every page as PNG into outDir (page0001.png ...). Page sizes come back in PDF points.</summary>
    Task<IReadOnlyList<RenderedPage>> RenderAsync(string pdfPath, double dpi, string outDir, CancellationToken ct);

    /// <summary>Renders a single page (0-based) to outPng.</summary>
    Task<RenderedPage> RenderPageAsync(string pdfPath, int pageIndex, double dpi, string outPng, CancellationToken ct);
}

public sealed record RenderedPage(string PngPath, double WidthPt, double HeightPt);

/// <summary>A recognised word. Box is in pixels of the image that was read.</summary>
public sealed record OcrWord(string Text, double X, double Y, double W, double H);

public sealed record OcrLine(IReadOnlyList<OcrWord> Words)
{
    public string Text => string.Join(" ", Words.Select(w => w.Text));
}

/// <summary>Reads text from an image. On Windows this is the OCR built into Windows 10/11.</summary>
public interface IOcrEngine
{
    Task<IReadOnlyList<OcrLine>> RecognizeAsync(string imagePath, CancellationToken ct);
}

/// <summary>Word/PowerPoint/Excel to PDF.</summary>
public interface IOfficeConverter
{
    Task ConvertToPdfAsync(string input, string outputPdf, CancellationToken ct);
}

/// <summary>Things an action may need to ask the person, or windows it may need to open.</summary>
public interface IUserPrompts
{
    /// <summary>Returns null if they cancelled. With confirm, asks twice and checks they match.</summary>
    Task<string?> AskPasswordAsync(string title, string message, bool confirm);

    void OpenStudio(IReadOnlyList<string> files);

    /// <summary>"How small?" in MB or % of the original. Returns null if cancelled.</summary>
    Task<SizeTarget?> AskTargetSizeAsync(long largestFileBytes, int fileCount, SizeTarget last);

    /// <summary>Pick the part of a clip to keep. previewFrame(seconds) returns a PNG path (or null for audio).</summary>
    Task<TrimRange?> AskTrimAsync(string file, double durationSeconds, bool hasVideo, Func<double, Task<string?>> previewFrame);

    /// <summary>Let the person put files in order. Returns the new order, or null if cancelled.</summary>
    Task<IReadOnlyList<string>?> AskOrderAsync(string title, string message, IReadOnlyList<string> files);

    /// <summary>Exact size / crop / percentage / icon. firstSize is the first image's pixel size, if known.</summary>
    Task<ResizeSpec?> AskResizeAsync(int fileCount, (int W, int H)? firstSize);

    /// <summary>Batch rename pattern, with a live preview of the new names.</summary>
    Task<RenameRequest?> AskRenameAsync(IReadOnlyList<string> files);

    /// <summary>Folder to save results in (when Settings say "ask every time"). Null if cancelled.</summary>
    Task<string?> AskFolderAsync(string title);
}
