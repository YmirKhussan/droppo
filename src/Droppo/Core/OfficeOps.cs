using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>Word, PowerPoint and Excel files.</summary>
public static class OfficeOps
{
    public static List<WheelAction> Actions(IReadOnlyList<string> files)
    {
        var list = new List<WheelAction>
        {
            new() { Id = "doc.pdf", Label = "PDF", Hint = "convert", PerFile = ToPdf },
        };
        if (files.Count > 1)
            list.Add(new() { Id = "doc.combine", Label = "1 PDF", Hint = "combine all", Combined = PdfOps.CombineWithImages });
        list.Add(new() { Id = "doc.studio", Label = "Studio", Hint = "arrange & edit", Combined = OpenStudio, Silent = true });
        list.AddRange(OtherOps.Actions(files).Where(a => a.Id != "any.pdf" && a.Id != "any.studio"));
        return list;
    }

    public static async Task<ActionOutput> ToPdf(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "", ".pdf");
        await ctx.OfficeConverter.ConvertToPdfAsync(input, output, ctx.Token);
        return new(output);
    }

    public static Task<ActionOutput> OpenStudio(IReadOnlyList<string> files, ActionContext ctx)
    {
        ctx.Ask.OpenStudio(files);
        return Task.FromResult(new ActionOutput(null));
    }
}

/// <summary>
/// LibreOffice in headless mode: free, and works when Microsoft Office isn't installed.
/// Each run gets its own throwaway profile so it doesn't clash with a LibreOffice window you have open.
/// </summary>
public sealed class LibreOfficeConverter : IOfficeConverter
{
    public string ExePath { get; }

    public LibreOfficeConverter(string exePath) => ExePath = exePath;

    public static LibreOfficeConverter? TryCreate(string? configured)
    {
        var extra = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"));
            extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"));
        }
        var path = ToolLocator.Locate("soffice", configured, extra);
        return path == null ? null : new LibreOfficeConverter(path);
    }

    public async Task ConvertToPdfAsync(string input, string outputPdf, CancellationToken ct)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var profile = new Uri(Path.Combine(tmp, "profile")).AbsoluteUri;
            var outDir = Path.Combine(tmp, "out");
            Directory.CreateDirectory(outDir);
            await ProcessRunner.RunAsync(ExePath, new[]
            {
                "-env:UserInstallation=" + profile, "--headless", "--norestore", "--nolockcheck",
                "--convert-to", "pdf", "--outdir", outDir, Path.GetFullPath(input),
            }, ct);

            // On Windows soffice can hand off to a background process and return early: wait for the file
            var produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".pdf");
            for (int i = 0; i < 600 && !IsComplete(produced); i++) await Task.Delay(100, ct);
            if (!File.Exists(produced)) throw new ToolException("LibreOffice couldn't convert this file.");
            File.Copy(produced, outputPdf, overwrite: true);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    private static bool IsComplete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return fs.Length > 0;
        }
        catch (IOException)
        {
            return false; // still being written
        }
    }
}
