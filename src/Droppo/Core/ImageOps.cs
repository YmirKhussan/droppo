using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

public static class ImageOps
{
    private static readonly string[] JpgExts = { ".jpg", ".jpeg", ".jfif" };

    public static List<WheelAction> Actions(Settings s, int count)
    {
        var list = new List<WheelAction>
        {
            new() { Id = "img.png", Label = "PNG", Hint = "convert", PerFile = ToPng, HideForExtensions = new[] { ".png" } },
            new() { Id = "img.jpg", Label = "JPG", Hint = "convert", PerFile = ToJpg, HideForExtensions = JpgExts },
            new() { Id = "img.webp", Label = "WebP", Hint = "convert", PerFile = ToWebp, HideForExtensions = new[] { ".webp" } },
            new() { Id = "img.pdf", Label = "PDF", Hint = count > 1 ? "combine A4" : "A4 page", Combined = ToPdf },
            CompressOps.Action("img.smaller", UnderTargetSize),
            ImageEditOps.ResizeAction(),
            new() { Id = "img.clean", Label = "Clean", Hint = "strip GPS/EXIF", PerFile = Clean, HideForExtensions = new[] { ".gif", ".bmp", ".ico" } },
        };
        if (count > 1)
        {
            list.Add(new() { Id = "img.studio", Label = "Arrange", Hint = "order into PDF", Combined = OfficeOps.OpenStudio, Silent = true });
            list.Add(ImageEditOps.RemoveBackgroundAction());
            list.Add(RenameOps.Action());
        }
        else
        {
            list.Add(new() { Id = "img.text", Label = "Text", Hint = "copy text (OCR)", PerFile = TextOps.FromImage });
            list.Add(ImageEditOps.RemoveBackgroundAction());
        }
        return list;
    }

    private static bool IsExt(string path, params string[] exts) =>
        exts.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Single still images go through the image2 muxer; -update 1 stops it treating "%" in names as a pattern.</summary>
    private static string[] StillOut(string output) => new[] { "-frames:v", "1", "-update", "1", output };

    public static async Task<ActionOutput> ToPng(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "", ".png");
        await ctx.Ff.RunAsync(new[] { "-i", input }.Concat(StillOut(output)), ctx.Token);
        return new(output);
    }

    public static async Task<ActionOutput> ToJpg(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "", ".jpg");
        await EncodeJpeg(ctx, input, output, quality: 2, scaleFilter: null);
        return new(output);
    }

    /// <summary>
    /// Any image to JPEG. Transparent areas become white (not black), phone rotation is applied.
    /// quality: 2 = best, 31 = smallest.
    /// </summary>
    public static Task EncodeJpeg(ActionContext ctx, string input, string output, int quality, string? scaleFilter)
    {
        var scale = scaleFilter == null ? "" : scaleFilter + ",";
        var graph = $"[0:v]{scale}format=rgba,split[a][b];[a]drawbox=c=white@1:t=fill:replace=1[bg];" +
                    "[bg][b]overlay=format=auto,format=yuvj420p[o]";
        var args = new List<string> { "-i", input, "-filter_complex", graph, "-map", "[o]", "-map_metadata", "-1", "-q:v", quality.ToString() };
        args.AddRange(StillOut(output));
        return ctx.Ff.RunAsync(args, ctx.Token);
    }

    public static async Task<ActionOutput> ToWebp(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "", ".webp");
        var args = new List<string> { "-i", input, "-c:v", "libwebp", "-quality", "85" };
        if (IsExt(input, ".gif")) args.AddRange(new[] { "-loop", "0", "-an" }); // keep the animation
        else args.AddRange(new[] { "-frames:v", "1" });
        args.Add(output);
        await ctx.Ff.RunAsync(args, ctx.Token);
        return new(output);
    }

    public static async Task<ActionOutput> ToIco(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "", ".ico");
        await ctx.Ff.RunAsync(new[]
        {
            "-i", input, "-frames:v", "1",
            "-vf", "scale=256:256:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba,pad=256:256:(ow-iw)/2:(oh-ih)/2:color=black@0",
            output,
        }, ctx.Token);
        return new(output);
    }

    public static async Task<ActionOutput> UnderTargetSize(string input, ActionContext ctx)
    {
        var want = CompressOps.Target(ctx);
        long original = new FileInfo(input).Length;
        long target = want.BytesFor(original);
        var name = Path.GetFileName(input);

        if (original <= target)
            return new(null, $"{name} is already {Paths.HumanSize(original)}, nothing to do");

        var tmp = Paths.NewTempDir();
        try
        {
            double[] scales = { 1.0, 0.75, 0.56, 0.42, 0.32, 0.24, 0.18 };
            for (int si = 0; si < scales.Length; si++)
            {
                double s = scales[si];
                string? scaleFilter = s >= 0.999 ? null : $"scale=trunc(iw*{Paths.Num(s)}):trunc(ih*{Paths.Num(s)}):flags=lanczos";
                var cache = new Dictionary<int, (string File, long Size)>();

                async Task<long> Try(int q)
                {
                    if (cache.TryGetValue(q, out var hit)) return hit.Size;
                    var f = Path.Combine(tmp, $"s{si}_q{q}.jpg");
                    await EncodeJpeg(ctx, input, f, q, scaleFilter);
                    var size = new FileInfo(f).Length;
                    cache[q] = (f, size);
                    return size;
                }

                if (await Try(31) > target) continue; // too big even at lowest quality, shrink and retry

                // smallest q (= best quality) that still fits
                int lo = 2, hi = 31;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (await Try(mid) <= target) hi = mid; else lo = mid + 1;
                }
                await Try(lo);

                var best = cache[lo];
                var output = Paths.NextTo(input, want.Suffix, ".jpg");
                File.Copy(best.File, output);
                var resized = s < 0.999 ? $", resized to {Math.Round(s * 100)}%" : "";
                return new(output, $"{name}: {Paths.HumanSize(original)} → {Paths.HumanSize(best.Size)}{resized}");
            }
            throw new InvalidOperationException($"Couldn't get it down to {want.Describe}. Try a bigger target.");
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    public static async Task<ActionOutput> Clean(string input, ActionContext ctx)
    {
        var name = Path.GetFileName(input);

        if (IsExt(input, JpgExts))
        {
            var output = Paths.NextTo(input, "-clean", Path.GetExtension(input));
            var bytes = await File.ReadAllBytesAsync(input, ctx.Token);
            await File.WriteAllBytesAsync(output, JpegTools.StripMetadata(bytes), ctx.Token);
            return new(output); // lossless
        }

        if (IsExt(input, ".png"))
        {
            var output = Paths.NextTo(input, "-clean", ".png");
            var bytes = await File.ReadAllBytesAsync(input, ctx.Token);
            await File.WriteAllBytesAsync(output, PngTools.StripMetadata(bytes), ctx.Token);
            return new(output); // lossless
        }

        if (IsExt(input, ".webp"))
        {
            var output = Paths.NextTo(input, "-clean", ".webp");
            await ctx.Ff.RunAsync(new[] { "-i", input, "-map_metadata", "-1", "-c:v", "libwebp", "-quality", "90", "-frames:v", "1", output }, ctx.Token);
            return new(output, $"{name}: re-saved without metadata");
        }

        if (IsExt(input, ".tif", ".tiff"))
        {
            var output = Paths.NextTo(input, "-clean", ".tiff");
            await ctx.Ff.RunAsync(new[] { "-i", input, "-map_metadata", "-1" }.Concat(StillOut(output)), ctx.Token);
            return new(output);
        }

        // heic / avif: ffmpeg can read them but not write them, so the clean copy is a JPG
        var jpg = Paths.NextTo(input, "-clean", ".jpg");
        await EncodeJpeg(ctx, input, jpg, 2, null);
        var raw = await File.ReadAllBytesAsync(jpg, ctx.Token);
        await File.WriteAllBytesAsync(jpg, JpegTools.StripMetadata(raw), ctx.Token);
        return new(jpg, $"{name}: saved as JPG without metadata");
    }

    public static async Task<ActionOutput> ToPdf(IReadOnlyList<string> files, ActionContext ctx)
    {
        var images = files.Where(File.Exists)
                          .OrderBy(f => Path.GetFileName(f), Paths.NaturalComparer.Instance)
                          .ToList();
        if (images.Count == 0) throw new InvalidOperationException("No images to put in the PDF.");

        int edge = ctx.Settings.PdfMaxImageEdge;
        var fit = $"scale=w='if(gt(iw,ih),min({edge},iw),-2)':h='if(gt(iw,ih),-2,min({edge},ih))':flags=lanczos";

        var tmp = Paths.NewTempDir();
        try
        {
            var pages = new List<byte[]>();
            for (int i = 0; i < images.Count; i++)
            {
                var jpg = Path.Combine(tmp, $"{i}.jpg");
                await EncodeJpeg(ctx, images[i], jpg, 3, fit);
                pages.Add(await File.ReadAllBytesAsync(jpg, ctx.Token));
            }

            var output = Paths.NextTo(images[0], images.Count > 1 ? "-combined" : "", ".pdf");
            PdfWriter.WriteImagesToA4(pages, output);
            var note = images.Count > 1 ? $"{images.Count} pages, {Paths.HumanSize(new FileInfo(output).Length)}" : null;
            return new(output, note);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }
}
