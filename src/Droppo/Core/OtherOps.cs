using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>Works on anything: folders, PDFs, docs, mixed selections.</summary>
public static class OtherOps
{
    public static List<WheelAction> Actions(IReadOnlyList<string> files)
    {
        var list = new List<WheelAction>();

        // Photos, PDFs and docs dragged together (e.g. IC scan + certificate): offer one combined PDF
        var kinds = files.Select(FileKinds.Of).Distinct().ToList();
        if (kinds.Count > 1 && kinds.All(k => k is FileKind.Image or FileKind.Pdf or FileKind.Office))
        {
            list.Add(new() { Id = "any.pdf", Label = "PDF", Hint = "combine all", Combined = PdfOps.CombineWithImages });
            list.Add(new() { Id = "any.studio", Label = "Studio", Hint = "arrange & edit", Combined = OfficeOps.OpenStudio, Silent = true });
        }

        list.Add(new() { Id = "any.zip", Label = "ZIP", Hint = "compress all", Combined = Zip });
        list.Add(RenameOps.Action());
        list.Add(new() { Id = "any.path", Label = "Path", Hint = "copy full path", ClipboardText = f => string.Join(Environment.NewLine, f) });
        list.Add(new() { Id = "any.name", Label = "Names", Hint = "copy file names", ClipboardText = f => string.Join(Environment.NewLine, f.Select(p => Path.GetFileName(p.TrimEnd('\\', '/')))) });
        return list;
    }

    public static Task<ActionOutput> Zip(IReadOnlyList<string> items, ActionContext ctx) => Task.Run(() =>
    {
        var existing = items.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) throw new InvalidOperationException("Nothing to zip.");

        var first = existing[0].TrimEnd('\\', '/');
        var dir = Paths.OutputFolderFor(first);
        var stem = Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first);
        var output = Paths.Unique(dir, stem, ".zip");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string UniqueEntry(string name)
        {
            var candidate = name;
            int i = 2;
            while (!used.Add(candidate))
            {
                var e = Path.GetExtension(name);
                candidate = $"{name[..^e.Length]} ({i++}){e}";
            }
            return candidate;
        }

        try
        {
            using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
            foreach (var item in existing)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var clean = item.TrimEnd('\\', '/');
                if (File.Exists(clean))
                {
                    zip.CreateEntryFromFile(clean, UniqueEntry(Path.GetFileName(clean)), CompressionLevel.Optimal);
                    continue;
                }

                var root = UniqueEntry(Path.GetFileName(clean));
                bool any = false;
                foreach (var file in Directory.EnumerateFiles(clean, "*", SearchOption.AllDirectories))
                {
                    ctx.Token.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(clean, file).Replace('\\', '/');
                    zip.CreateEntryFromFile(file, $"{root}/{rel}", CompressionLevel.Optimal);
                    any = true;
                }
                if (!any) zip.CreateEntry(root + "/"); // keep empty folders
            }
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }

        return new ActionOutput(output, $"{Path.GetFileName(output)}, {Paths.HumanSize(new FileInfo(output).Length)}");
    });
}
