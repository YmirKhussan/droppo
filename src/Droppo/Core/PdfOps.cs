using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>
/// PDF actions. Merge/split/rotate go through qpdf and never touch page content.
/// Page images come from the system PDF renderer.
/// </summary>
public static class PdfOps
{
    public static List<WheelAction> Actions(Settings s, int count)
    {
        var list = new List<WheelAction>
        {
            new() { Id = "pdf.studio", Label = "Studio", Hint = "edit & arrange", Combined = OfficeOps.OpenStudio, Silent = true },
        };
        if (count > 1)
            list.Add(new() { Id = "pdf.merge", Label = "Merge", Hint = "set the order", Combined = OfficeOps.OpenStudio, Silent = true });
        list.Add(new() { Id = "pdf.jpg", Label = "JPG", Hint = "each page", PerFile = Unlocked(ToJpg) });
        list.Add(new() { Id = "pdf.png", Label = "PNG", Hint = "each page", PerFile = Unlocked(ToPng) });
        list.Add(new() { Id = "pdf.split", Label = "Split", Hint = "1 PDF per page", PerFile = Unlocked(Split) });
        list.Add(CompressOps.Action("pdf.smaller", Unlocked(UnderTargetSize)));
        list.Add(new() { Id = "pdf.lock", Label = "Lock", Hint = "add/remove password", PerFile = LockOrUnlock });
        list.Add(new() { Id = "pdf.text", Label = "Text", Hint = "copy all text", PerFile = Unlocked(TextOps.FromPdf) });
        list.Add(new() { Id = "pdf.rotate", Label = "Rotate", Hint = "90° clockwise", PerFile = Unlocked(Rotate) });
        if (count > 1) list.Add(RenameOps.Action());
        return list;
    }

    /// <summary>
    /// Wraps a PDF action so password-protected files just work: ask for the password, run the
    /// action on an unlocked temporary copy, then move the result next to the original.
    /// </summary>
    public static Func<string, ActionContext, Task<ActionOutput>> Unlocked(Func<string, ActionContext, Task<ActionOutput>> inner) =>
        async (input, ctx) =>
        {
            if (ctx.Qpdf == null || !await ctx.Qpdf.IsEncryptedAsync(input, ctx.Token)) return await inner(input, ctx);

            var tmp = Paths.NewTempDir();
            try
            {
                var copy = Path.Combine(tmp, Path.GetFileName(input)); // same name, so outputs are named the same
                var name = Path.GetFileName(input);
                bool opened = false;
                if (!await ctx.Qpdf.RequiresPasswordAsync(input, ctx.Token))
                {
                    opened = await ctx.Qpdf.TryDecryptAsync(input, "", copy, ctx.Token);
                }
                else
                {
                    var message = $"\"{name}\" is password-protected. Enter its password:";
                    for (int attempt = 0; attempt < 3 && !opened; attempt++)
                    {
                        var password = await ctx.Ask.AskPasswordAsync("Password needed", message, confirm: false);
                        if (password == null) return new(null, $"{name}: cancelled");
                        opened = await ctx.Qpdf.TryDecryptAsync(input, password, copy, ctx.Token);
                        message = $"Wrong password for \"{name}\". Try again:";
                    }
                }
                if (!opened) throw new InvalidOperationException("Wrong password.");

                var result = await inner(copy, ctx);
                if (result.Path == null) return result;

                // Bring the result out of the temp folder, to where results go
                var dir = Paths.OutputFolderFor(input);
                var producedIn = Path.GetDirectoryName(Path.GetFullPath(result.Path)) ?? "";
                if (string.Equals(producedIn.TrimEnd('\\', '/'), Path.GetFullPath(dir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return result; // already saved in the output folder
                string final;
                if (Directory.Exists(result.Path))
                {
                    final = Path.Combine(dir, Path.GetFileName(result.Path));
                    for (int i = 1; Directory.Exists(final) || File.Exists(final); i++)
                        final = Path.Combine(dir, $"{Path.GetFileName(result.Path)} ({i})");
                    Directory.CreateDirectory(final); // file-by-file: works across drives, unlike Directory.Move
                    foreach (var f in Directory.GetFiles(result.Path))
                        File.Move(f, Path.Combine(final, Path.GetFileName(f)));
                }
                else
                {
                    final = Paths.Unique(dir, Path.GetFileNameWithoutExtension(result.Path), Path.GetExtension(result.Path));
                    File.Move(result.Path, final);
                }
                return result with { Path = final };
            }
            finally
            {
                Paths.TryDelete(tmp);
            }
        };

    // ---------- password ----------

    /// <summary>Locked PDF: asks for the password and saves an unlocked copy. Unlocked PDF: asks for a new one.</summary>
    public static async Task<ActionOutput> LockOrUnlock(string input, ActionContext ctx)
    {
        var name = Path.GetFileName(input);
        var q = ctx.Q;

        if (!await q.IsEncryptedAsync(input, ctx.Token))
        {
            var password = await ctx.Ask.AskPasswordAsync("Protect PDF", $"Choose a password for \"{name}\":", confirm: true);
            if (string.IsNullOrEmpty(password)) return new(null, "Cancelled, nothing changed.");
            var locked = Paths.NextTo(input, "-protected", ".pdf");
            await q.EncryptAsync(input, password, locked, ctx.Token);
            return new(locked, $"{Path.GetFileName(locked)} needs the password to open");
        }

        var unlocked = Paths.NextTo(input, "-unlocked", ".pdf");
        if (!await q.RequiresPasswordAsync(input, ctx.Token))
        {
            await q.TryDecryptAsync(input, "", unlocked, ctx.Token); // only had print/copy restrictions
            return new(unlocked, "Print and copy restrictions removed");
        }

        var message = $"\"{name}\" is locked. Enter its password to save an unlocked copy:";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var password = await ctx.Ask.AskPasswordAsync("Unlock PDF", message, confirm: false);
            if (password == null) return new(null, "Cancelled, nothing changed.");
            if (await q.TryDecryptAsync(input, password, unlocked, ctx.Token)) return new(unlocked);
            message = "Wrong password. Try again:";
        }
        throw new InvalidOperationException("Wrong password.");
    }

    /// <summary>One image as an A4 PDF page (used for combining and for Studio).</summary>
    public static async Task ImageToA4PdfAsync(ActionContext ctx, string image, string outPdf)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var jpg = Path.Combine(tmp, "page.jpg");
            int edge = ctx.Settings.PdfMaxImageEdge;
            await ImageOps.EncodeJpeg(ctx, image, jpg, 3,
                $"scale=w='if(gt(iw,ih),min({edge},iw),-2)':h='if(gt(iw,ih),-2,min({edge},ih))':flags=lanczos");
            PdfWriter.WriteImagesToA4(new[] { await File.ReadAllBytesAsync(jpg, ctx.Token) }, outPdf);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // ---------- pages to images ----------

    public static Task<ActionOutput> ToPng(string input, ActionContext ctx) => PagesToImages(input, ctx, ".png");

    public static Task<ActionOutput> ToJpg(string input, ActionContext ctx) => PagesToImages(input, ctx, ".jpg");

    private static async Task<ActionOutput> PagesToImages(string input, ActionContext ctx, string ext)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var pages = await ctx.Renderer.RenderAsync(input, ctx.Settings.PdfImageDpi, tmp, ctx.Token);
            if (pages.Count == 0) throw new InvalidOperationException("This PDF has no pages.");

            async Task Save(RenderedPage page, string dest)
            {
                if (ext == ".png") File.Copy(page.PngPath, dest);
                else await ImageOps.EncodeJpeg(ctx, page.PngPath, dest, 2, null);
            }

            if (pages.Count == 1)
            {
                var single = Paths.NextTo(input, "", ext);
                await Save(pages[0], single);
                return new(single);
            }

            // Many pages: keep them together in their own folder instead of spraying the original folder
            var folder = Paths.NewFolderNextTo(input, " pages");
            var stem = Path.GetFileNameWithoutExtension(input);
            int digits = pages.Count.ToString().Length;
            for (int i = 0; i < pages.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                await Save(pages[i], Path.Combine(folder, $"{stem}-{(i + 1).ToString().PadLeft(digits, '0')}{ext}"));
            }
            return new(folder, $"{pages.Count} pages saved in \"{Path.GetFileName(folder)}\"");
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // ---------- qpdf: merge, split, rotate ----------

    public static async Task<ActionOutput> Merge(IReadOnlyList<string> files, ActionContext ctx)
    {
        var pdfs = SortedExisting(files);
        if (pdfs.Count < 2) throw new InvalidOperationException("Drag two or more PDFs to merge them.");
        var output = Paths.NextTo(pdfs[0], "-merged", ".pdf");
        await MergeInto(ctx, pdfs, output);
        return new(output, $"{pdfs.Count} PDFs merged in name order");
    }

    internal static Task MergeInto(ActionContext ctx, IReadOnlyList<string> pdfs, string output)
    {
        var args = new List<string> { "--empty", "--pages" };
        args.AddRange(pdfs);
        args.Add("--");
        args.Add(output);
        return ctx.Q.RunWithArgFileAsync(args, ctx.Token);
    }

    public static async Task<ActionOutput> Split(string input, ActionContext ctx)
    {
        int count = await ctx.Q.PageCountAsync(input, ctx.Token);
        if (count == 1) return new(null, $"{Path.GetFileName(input)} only has one page");

        var folder = Paths.NewFolderNextTo(input, " pages");
        // qpdf names them "name-1.pdf", "name-2.pdf"... (zero-padded when there are many)
        await ctx.Q.RunAsync(new[] { "--split-pages", input, Path.Combine(folder, Path.GetFileName(input)) }, ctx.Token);
        return new(folder, $"{(count > 0 ? count.ToString() : "All")} pages saved in \"{Path.GetFileName(folder)}\"");
    }

    public static async Task<ActionOutput> Rotate(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "-rotated", ".pdf");
        await ctx.Q.RunAsync(new[] { "--rotate=+90", input, output }, ctx.Token);
        return new(output);
    }

    // ---------- fit under X MB ----------

    /// <summary>
    /// First tries a lossless repack (text stays selectable). If that's not enough, renders the pages
    /// and rebuilds the PDF from JPEGs, lowering quality and resolution until it fits. That second
    /// step is what scanned documents for upload portals need.
    /// </summary>
    public static async Task<ActionOutput> UnderTargetSize(string input, ActionContext ctx)
    {
        var want = CompressOps.Target(ctx);
        long original = new FileInfo(input).Length;
        long target = want.BytesFor(original);
        var name = Path.GetFileName(input);
        if (original <= target) return new(null, $"{name} is already {Paths.HumanSize(original)}, nothing to do");

        var output = Paths.NextTo(input, want.Suffix, ".pdf");
        var tmp = Paths.NewTempDir();
        // smallest version seen so far, in case the target turns out to be unreachable
        (string File, long Size, bool Flattened)? smallest = null;
        void Consider(string file, long size, bool flattened)
        {
            if (smallest == null || size < smallest.Value.Size) smallest = (file, size, flattened);
        }

        try
        {
            if (ctx.Qpdf != null)
            {
                var repacked = Path.Combine(tmp, "repacked.pdf");
                await ctx.Qpdf.RunAsync(new[]
                {
                    "--object-streams=generate", "--compress-streams=y", "--recompress-flate", "--compression-level=9",
                    input, repacked,
                }, ctx.Token);
                long size = new FileInfo(repacked).Length;
                if (size <= target)
                {
                    File.Copy(repacked, output);
                    return new(output, $"{name}: {Paths.HumanSize(original)} → {Paths.HumanSize(size)}, text kept");
                }
                Consider(repacked, size, flattened: false);
            }

            foreach (int dpi in new[] { 150, 110, 80, 60 })
            {
                var renderDir = Path.Combine(tmp, $"r{dpi}");
                Directory.CreateDirectory(renderDir);
                var pages = await ctx.Renderer.RenderAsync(input, dpi, renderDir, ctx.Token);
                if (pages.Count == 0) throw new InvalidOperationException("This PDF has no pages.");

                var cache = new Dictionary<int, (string File, long Size)>();
                async Task<long> Try(int q)
                {
                    if (cache.TryGetValue(q, out var hit)) return hit.Size;
                    var jpgs = new List<(byte[] Jpeg, double WidthPt, double HeightPt)>();
                    for (int i = 0; i < pages.Count; i++)
                    {
                        ctx.Token.ThrowIfCancellationRequested();
                        var jpg = Path.Combine(renderDir, $"q{q}_{i}.jpg");
                        await ImageOps.EncodeJpeg(ctx, pages[i].PngPath, jpg, q, null);
                        jpgs.Add((await File.ReadAllBytesAsync(jpg, ctx.Token), pages[i].WidthPt, pages[i].HeightPt));
                        File.Delete(jpg);
                    }
                    var pdf = Path.Combine(tmp, $"d{dpi}_q{q}.pdf");
                    PdfWriter.WriteFullPages(jpgs, pdf);
                    var size = new FileInfo(pdf).Length;
                    cache[q] = (pdf, size);
                    return size;
                }

                long lowest = await Try(31);
                Consider(cache[31].File, lowest, flattened: true);
                if (lowest > target) continue;

                int lo = 2, hi = 31;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (await Try(mid) <= target) hi = mid; else lo = mid + 1;
                }
                await Try(lo);

                var best = cache[lo];
                File.Copy(best.File, output);
                return new(output, $"{name}: {Paths.HumanSize(original)} → {Paths.HumanSize(best.Size)}. " +
                                   "Pages were flattened to images, so text can't be selected any more.");
            }
            // Target out of reach. Usually a text PDF that's already compact: still hand over the smallest
            // version if it's meaningfully smaller, and say how close it got.
            if (smallest is { } best0 && best0.Size < original * 0.97)
            {
                File.Copy(best0.File, output);
                var how = best0.Flattened ? " Pages were flattened to images, so text can't be selected any more." : " Text kept.";
                return new(output, $"{name}: couldn't reach {want.Describe}. Smallest possible is {Paths.HumanSize(best0.Size)} " +
                                   $"({Math.Round(100.0 * best0.Size / original)}% of the original).{how}");
            }
            return new(null, $"{name} is already about as small as a PDF like this gets ({Paths.HumanSize(original)}).");
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // ---------- images + PDFs together ----------

    /// <summary>
    /// Drag photos, PDFs and Office docs together: images become A4 pages, docs get converted,
    /// everything merges in name order. (Use Studio to pick a different order.)
    /// </summary>
    public static async Task<ActionOutput> CombineWithImages(IReadOnlyList<string> files, ActionContext ctx)
    {
        var items = SortedExisting(files);
        var tmp = Paths.NewTempDir();
        try
        {
            var parts = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var part = Path.Combine(tmp, $"{i}.pdf");
                switch (FileKinds.Of(items[i]))
                {
                    case FileKind.Pdf:
                        parts.Add(items[i]);
                        continue;
                    case FileKind.Office:
                        await ctx.OfficeConverter.ConvertToPdfAsync(items[i], part, ctx.Token);
                        break;
                    case FileKind.Image:
                        await ImageToA4PdfAsync(ctx, items[i], part);
                        break;
                    default:
                        continue;
                }
                parts.Add(part);
            }
            if (parts.Count == 0) throw new InvalidOperationException("Nothing here can go into a PDF.");

            var output = Paths.NextTo(items[0], "-combined", ".pdf");
            await MergeInto(ctx, parts, output);
            return new(output, $"{items.Count} files combined in name order");
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    private static List<string> SortedExisting(IReadOnlyList<string> files) =>
        files.Where(File.Exists).OrderBy(f => Path.GetFileName(f), Paths.NaturalComparer.Instance).ToList();
}
