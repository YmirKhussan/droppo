using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>What one action produced for one file (or for the whole batch).</summary>
public sealed record ActionOutput(string? Path, string? Note = null, string? Clipboard = null);

public sealed class ActionContext
{
    public FFmpeg? FFmpeg { get; init; }
    public Qpdf? Qpdf { get; init; }
    public IPdfRenderer? PdfRenderer { get; init; }
    public IOcrEngine? OcrEngine { get; init; }
    public IOfficeConverter? Office { get; init; }
    public IUserPrompts? Prompts { get; init; }
    public required Settings Settings { get; init; }
    public CancellationToken Token { get; init; }

    /// <summary>Whatever the action's Prepare step asked for (e.g. the target size), shared by every file.</summary>
    public object? State { get; set; }

    /// <summary>Called once questions are answered and the real work begins (the app uses it for "Working on it...").</summary>
    public Action? WorkStarted { get; set; }

    public FFmpeg Ff => FFmpeg ?? throw ToolMissingException.FFmpeg();
    public Qpdf Q => Qpdf ?? throw ToolMissingException.Qpdf();
    public IPdfRenderer Renderer => PdfRenderer ?? throw ToolMissingException.PdfRenderer();
    public IOcrEngine Ocr => OcrEngine ?? throw new ToolMissingException("Text recognition (OCR) isn't available. Windows needs an OCR language installed (Settings > Language).");
    public IOfficeConverter OfficeConverter => Office ?? throw new ToolMissingException("Office to PDF needs Microsoft Office or LibreOffice installed.");
    public IUserPrompts Ask => Prompts ?? throw new InvalidOperationException("This action needs a window and isn't available here.");
}

/// <summary>One slice of the wheel.</summary>
public sealed class WheelAction
{
    public required string Id { get; init; }

    /// <summary>Big text on the slice, e.g. "PNG".</summary>
    public required string Label { get; init; }

    /// <summary>Small text under it, e.g. "convert".</summary>
    public required string Hint { get; init; }

    /// <summary>Runs once per dropped file.</summary>
    public Func<string, ActionContext, Task<ActionOutput>>? PerFile { get; init; }

    /// <summary>Runs once with all dropped files (merge into PDF, zip...).</summary>
    public Func<IReadOnlyList<string>, ActionContext, Task<ActionOutput>>? Combined { get; init; }

    /// <summary>Produces text for the clipboard instead of a file.</summary>
    public Func<IReadOnlyList<string>, string>? ClipboardText { get; init; }

    /// <summary>Hide this slice when every input already has one of these extensions (e.g. no "PNG" for PNGs).</summary>
    public string[] HideForExtensions { get; init; } = Array.Empty<string>();

    /// <summary>Runs once before the files (e.g. ask "how small?"). Returning null cancels the action.</summary>
    public Func<IReadOnlyList<string>, ActionContext, Task<object?>>? Prepare { get; init; }

    /// <summary>Changes files where they are (rename): the output-folder setting doesn't apply.</summary>
    public bool InPlace { get; init; }

    /// <summary>Opens a window instead of producing a file, so there's no "done" notification.</summary>
    public bool Silent { get; init; }
}

public sealed class ActionResult
{
    public List<string> Outputs { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Errors { get; } = new();
    public string? ClipboardText { get; set; }

    /// <summary>The person cancelled a question the action asked: nothing to report.</summary>
    public bool Cancelled { get; set; }

    public bool Ok => Errors.Count == 0;
}

public static class ActionRunner
{
    public static async Task<ActionResult> RunAsync(WheelAction action, IReadOnlyList<string> files, ActionContext ctx)
    {
        var result = new ActionResult();
        try
        {
            if (action.Prepare != null)
            {
                var state = await action.Prepare(files, ctx).ConfigureAwait(false);
                if (state == null)
                {
                    result.Cancelled = true;
                    return result;
                }
                ctx.State = state;
            }

            // Where results go (Settings > Output). Scoped to this run only.
            if (!action.Silent && !action.InPlace && action.ClipboardText == null)
            {
                switch (ctx.Settings.OutputMode)
                {
                    case "Folder" when !string.IsNullOrWhiteSpace(ctx.Settings.OutputFolder):
                        Paths.OutputOverride = ctx.Settings.OutputFolder;
                        break;
                    case "Ask":
                        var folder = await ctx.Ask.AskFolderAsync("Where should we save it?").ConfigureAwait(false);
                        if (folder == null) { result.Cancelled = true; return result; }
                        Paths.OutputOverride = folder;
                        break;
                }
            }

            ctx.WorkStarted?.Invoke();

            if (action.ClipboardText != null)
            {
                result.ClipboardText = action.ClipboardText(files);
            }
            else if (action.Combined != null)
            {
                Collect(result, await action.Combined(files, ctx).ConfigureAwait(false));
            }
            else if (action.PerFile != null)
            {
                foreach (var file in files)
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    if (!File.Exists(file)) continue;
                    try
                    {
                        Collect(result, await action.PerFile(file, ctx).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                        Log.Write($"{action.Id} failed on {file}: {ex}");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Errors.Add("Cancelled.");
        }
        catch (Exception ex)
        {
            result.Errors.Add(ex.Message);
            Log.Write($"{action.Id} failed: {ex}");
        }
        return result;
    }

    private static void Collect(ActionResult result, ActionOutput output)
    {
        if (output.Path != null) result.Outputs.Add(output.Path);
        if (output.Note != null) result.Notes.Add(output.Note);
        if (output.Clipboard != null)
            result.ClipboardText = result.ClipboardText == null ? output.Clipboard : result.ClipboardText + "\n\n" + output.Clipboard;
    }
}

public static class ActionRegistry
{
    public const int MaxSlices = 10;

    /// <summary>The slices to show for whatever was dropped.</summary>
    public static IReadOnlyList<WheelAction> For(IReadOnlyList<string> files, Settings settings)
    {
        var kind = FileKinds.OfAll(files);
        var all = kind switch
        {
            FileKind.Image => ImageOps.Actions(settings, files.Count),
            FileKind.Video => AvOps.VideoActions(settings, files.Count),
            FileKind.Audio => AvOps.AudioActions(files.Count),
            FileKind.Pdf => PdfOps.Actions(settings, files.Count),
            FileKind.Office => OfficeOps.Actions(files),
            FileKind.None => new List<WheelAction>(),
            _ => OtherOps.Actions(files),
        };

        var exts = files.Select(f => Path.GetExtension(f).ToLowerInvariant()).Distinct().ToList();
        return all
            .Where(a => a.HideForExtensions.Length == 0 || !exts.All(e => a.HideForExtensions.Contains(e)))
            .Take(MaxSlices)
            .ToList();
    }
}
