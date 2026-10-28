using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core.Studio;

/// <summary>Turns dropped files (PDFs, images, Office docs) into Studio pages with thumbnails.</summary>
public static class StudioLoader
{
    public const double ThumbDpi = 40;

    public static async Task<List<StudioPage>> LoadAsync(IReadOnlyList<string> files, ActionContext ctx, string workDir, IProgress<string>? progress = null)
    {
        var pages = new List<StudioPage>();
        foreach (var file in files)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            progress?.Report($"Opening {name}...");

            var pdf = await ToPdfAsync(file, ctx, workDir);
            if (pdf == null) continue;

            var thumbDir = Path.Combine(workDir, "thumbs-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(thumbDir);
            var rendered = await ctx.Renderer.RenderAsync(pdf, ThumbDpi, thumbDir, ctx.Token);
            var fields = ctx.Qpdf != null ? await PdfForms.ReadAsync(ctx.Qpdf, pdf, ctx.Token) : new List<FormField>();
            for (int i = 0; i < rendered.Count; i++)
            {
                pages.Add(new StudioPage
                {
                    SourcePdf = pdf, SourceIndex = i, SourceName = name, OriginalPath = file,
                    WidthPt = rendered[i].WidthPt, HeightPt = rendered[i].HeightPt,
                    ThumbPath = rendered[i].PngPath,
                    Fields = fields.Where(f => f.PageIndex == i).ToList(),
                });
            }
        }
        return pages;
    }

    /// <summary>Returns a PDF Studio can work with, or null for file types it can't take.</summary>
    private static async Task<string?> ToPdfAsync(string file, ActionContext ctx, string workDir)
    {
        var name = Path.GetFileName(file);
        var temp = Path.Combine(workDir, Guid.NewGuid().ToString("N")[..8] + ".pdf");

        switch (FileKinds.Of(file))
        {
            case FileKind.Image:
                await PdfOps.ImageToA4PdfAsync(ctx, file, temp);
                return temp;

            case FileKind.Office:
                await ctx.OfficeConverter.ConvertToPdfAsync(file, temp, ctx.Token);
                return temp;

            case FileKind.Pdf:
                if (ctx.Qpdf == null || !await ctx.Qpdf.IsEncryptedAsync(file, ctx.Token)) return file;

                if (!await ctx.Qpdf.RequiresPasswordAsync(file, ctx.Token))
                {
                    // Only print/copy restrictions: open freely, but work on an unrestricted copy
                    await ctx.Qpdf.TryDecryptAsync(file, "", temp, ctx.Token);
                    return temp;
                }

                var message = $"\"{name}\" is password-protected. Enter its password:";
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var password = await ctx.Ask.AskPasswordAsync("Password needed", message, confirm: false);
                    if (password == null) return null;
                    if (await ctx.Qpdf.TryDecryptAsync(file, password, temp, ctx.Token)) return temp;
                    message = $"Wrong password for \"{name}\". Try again:";
                }
                throw new InvalidOperationException($"Couldn't unlock {name}.");

            default:
                return null;
        }
    }
}

/// <summary>
/// Builds the final PDF from a Studio document. Order of work:
/// pick pages in order → straighten existing rotation → draw edits → apply Studio rotation →
/// stamp page numbers/watermark → optional password. Originals are never modified.
/// </summary>
public static class StudioExporter
{
    public const double OcrDpi = 200;

    public static async Task ExportAsync(StudioDocument doc, IReadOnlyList<StudioPage> pages, string outPath, ActionContext ctx, IProgress<string>? progress = null)
    {
        if (pages.Count == 0) throw new InvalidOperationException("There are no pages to save.");
        var q = ctx.Q;
        var tmp = Paths.NewTempDir();
        try
        {
            int step = 0;
            string Next() => Path.Combine(tmp, $"step{++step}.pdf");

            // 0. Forms someone filled in: bake the form into the page so our answers can sit on top
            var flattened = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in pages.Where(p => p.Fields.Any(f => f.Changed)).Select(p => p.SourcePdf).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                progress?.Report("Filling in the form...");
                var flat = Path.Combine(tmp, $"form-{flattened.Count}.pdf");
                await q.RunAsync(new[] { "--generate-appearances", "--flatten-annotations=all", source, flat }, ctx.Token);
                flattened[source] = flat;
            }

            // 1. Pages in the chosen order
            progress?.Report("Putting pages in order...");
            var assembled = Next();
            var args = new List<string> { "--empty", "--pages" };
            foreach (var p in pages) { args.Add(flattened.TryGetValue(p.SourcePdf, out var f) ? f : p.SourcePdf); args.Add((p.SourceIndex + 1).ToString()); }
            args.Add("--");
            args.Add(assembled);
            await q.RunWithArgFileAsync(args, ctx.Token);

            // 2. Bake in any rotation the pages already had, so our coordinates match what you saw
            var current = Next();
            await q.RunAsync(new[] { assembled, "--flatten-rotation", current }, ctx.Token);

            // 3. Edits and invisible OCR text
            if (pages.Any(p => p.Edits.Count > 0 || p.Fields.Any(f => f.Changed)) || doc.MakeSearchable)
            {
                var overlay = new OverlayPdf();
                var images = new Dictionary<string, PdfImage>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < pages.Count; i++)
                {
                    int idx = overlay.AddPage(pages[i].WidthPt, pages[i].HeightPt);
                    if (flattened.ContainsKey(pages[i].SourcePdf))
                        foreach (var field in pages[i].Fields.Where(f => f.Changed))
                            PdfForms.Draw(overlay, idx, field);
                    foreach (var edit in pages[i].Edits)
                        await DrawEdit(overlay, idx, edit, images, ctx);
                }

                if (doc.MakeSearchable)
                {
                    progress?.Report("Reading text (OCR)...");
                    var renderDir = Path.Combine(tmp, "ocr");
                    Directory.CreateDirectory(renderDir);
                    var rendered = await ctx.Renderer.RenderAsync(current, OcrDpi, renderDir, ctx.Token);
                    var hasText = await q.PagesWithFontsAsync(current, ctx.Token);
                    double toPt = 72.0 / OcrDpi;
                    for (int i = 0; i < rendered.Count && i < pages.Count; i++)
                    {
                        if (i < hasText.Length && hasText[i]) continue; // already searchable, don't double up
                        progress?.Report($"Reading text (OCR) {i + 1}/{rendered.Count}...");
                        foreach (var line in await ctx.Ocr.RecognizeAsync(rendered[i].PngPath, ctx.Token))
                            foreach (var w in line.Words)
                                overlay.InvisibleWord(i, w.X * toPt, w.Y * toPt, w.W * toPt, w.H * toPt, w.Text);
                    }
                }

                if (overlay.HasContent)
                {
                    progress?.Report("Drawing your edits...");
                    var ovPath = Path.Combine(tmp, "edits-overlay.pdf");
                    overlay.Save(ovPath);
                    var withEdits = Next();
                    await q.RunAsync(new[] { current, "--overlay", ovPath, "--", withEdits }, ctx.Token);
                    current = withEdits;
                }
            }

            // 4. Rotation added in Studio (turns the page and everything drawn on it together)
            var rotated = pages.Select((p, i) => (p, i)).Where(x => x.p.UserRotation != 0).ToList();
            if (rotated.Count > 0)
            {
                var rotArgs = new List<string> { current };
                foreach (var group in rotated.GroupBy(x => x.p.UserRotation))
                    rotArgs.Add($"--rotate=+{group.Key}:{string.Join(",", group.Select(x => x.i + 1))}");
                var turned = Next();
                rotArgs.Add(turned);
                await q.RunWithArgFileAsync(rotArgs, ctx.Token);
                current = turned;
            }

            // 5. Page numbers / watermark, placed on the page as it finally looks
            if (doc.PageNumbers != null || doc.Watermark != null)
            {
                progress?.Report("Stamping pages...");
                if (rotated.Count > 0)
                {
                    var flat = Next();
                    await q.RunAsync(new[] { current, "--flatten-rotation", flat }, ctx.Token);
                    current = flat;
                }

                var stamps = new OverlayPdf();
                for (int i = 0; i < pages.Count; i++)
                {
                    double w = pages[i].DisplayWidth, h = pages[i].DisplayHeight;
                    int idx = stamps.AddPage(w, h);
                    if (doc.Watermark != null) DrawWatermark(stamps, idx, w, h, doc.Watermark);
                    if (doc.PageNumbers != null) DrawPageNumber(stamps, idx, w, h, i, pages.Count, doc.PageNumbers);
                }
                var stampPath = Path.Combine(tmp, "stamps-overlay.pdf");
                stamps.Save(stampPath);
                var stamped = Next();
                await q.RunAsync(new[] { current, "--overlay", stampPath, "--", stamped }, ctx.Token);
                current = stamped;
            }

            // 6. Password
            progress?.Report("Saving...");
            if (!string.IsNullOrEmpty(doc.Password))
                await q.EncryptAsync(current, doc.Password, outPath, ctx.Token);
            else
                File.Copy(current, outPath, overwrite: true);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    private static async Task DrawEdit(OverlayPdf overlay, int page, PageEdit edit, Dictionary<string, PdfImage> images, ActionContext ctx)
    {
        switch (edit)
        {
            case RectEdit r:
                overlay.FillRect(page, r.X, r.Y, r.W, r.H, r.Color, r.Opacity);
                break;
            case TextEdit t when !string.IsNullOrWhiteSpace(t.Text):
                overlay.Text(page, t.X, t.Y, t.Text, t.FontSize, t.Color, t.Rotation, fontFile: t.FontFile);
                break;
            case SignatureEdit sig:
                foreach (var stroke in sig.PageStrokes())
                    overlay.Ink(page, stroke, sig.PenWidth, sig.Color);
                break;
            case InkEdit ink:
                overlay.Ink(page, ink.Points, ink.Width, ink.Color);
                break;
            case ImageEdit im when File.Exists(im.ImagePath):
                if (!images.TryGetValue(im.ImagePath, out var pdfImage))
                {
                    pdfImage = await OverlayPdf.LoadImageAsync(ctx, im.ImagePath);
                    images[im.ImagePath] = pdfImage;
                }
                overlay.Image(page, im.X, im.Y, im.W, im.H, pdfImage, im.Rotation);
                break;
        }
    }

    private static void DrawPageNumber(OverlayPdf o, int page, double w, double h, int index, int total, PageNumberOptions opts)
    {
        int n = index + opts.StartAt;
        var text = opts.Format.Replace("{n}", n.ToString()).Replace("{total}", (total + opts.StartAt - 1).ToString());
        double size = opts.FontSize, tw = OverlayPdf.TextWidth(text, size, false), margin = 30;
        var (cx, cy) = opts.Position switch
        {
            NumberPosition.BottomRight => (w - margin - tw / 2, h - margin * 0.8),
            NumberPosition.TopRight => (w - margin - tw / 2, margin * 0.8),
            _ => (w / 2, h - margin * 0.8),
        };
        o.TextCentered(page, cx, cy, text, size, Rgb.Gray, 0, bold: false, alpha: 1);
    }

    private static void DrawWatermark(OverlayPdf o, int page, double w, double h, WatermarkOptions opts)
    {
        if (string.IsNullOrWhiteSpace(opts.Text)) return;
        double angle = opts.Diagonal ? -Math.Atan2(h, w) * 180 / Math.PI : 0;
        double room = opts.Diagonal ? Math.Sqrt(w * w + h * h) * 0.75 : w * 0.8;
        double width72 = OverlayPdf.TextWidth(opts.Text, 72, true);
        double size = width72 > 0 ? Math.Min(110, 72 * room / width72) : 72;
        o.TextCentered(page, w / 2, h / 2, opts.Text, size, opts.Color, angle, bold: true, alpha: opts.Opacity);
    }
}

/// <summary>Helpers the Studio editor calls while you edit.</summary>
public static class StudioTools
{
    /// <summary>A one-page blank PDF of the given size (for "Insert blank page").</summary>
    public static string CreateBlankPdf(string workDir, double widthPt, double heightPt)
    {
        var path = Path.Combine(workDir, "blank-" + Guid.NewGuid().ToString("N")[..8] + ".pdf");
        var pdf = new OverlayPdf();
        pdf.AddPage(widthPt, heightPt);
        pdf.Save(path);
        return path;
    }

    /// <summary>
    /// The page colour around a box (points), so white-out and Replace patches blend in on cream paper,
    /// grey forms or coloured headers. Samples thin strips just outside all four sides and takes the
    /// median, so one side touching text or a line doesn't throw it off.
    /// </summary>
    public static async Task<Rgb> SampleBackgroundAsync(ActionContext ctx, string pagePng, double dpi, double x, double y, double w, double h)
    {
        var (imgW, imgH) = PngTools.ReadSize(pagePng);
        double s = dpi / 72.0;
        int bx = (int)Math.Floor(x * s), by = (int)Math.Floor(y * s);
        int bw = Math.Max(1, (int)Math.Ceiling(w * s)), bh = Math.Max(1, (int)Math.Ceiling(h * s));
        const int gap = 3, thick = 2;

        // (x, y, w, h) of each strip, clipped to the image; skipped if it falls off the page
        var strips = new List<(int X, int Y, int W, int H)>
        {
            (bx, by - gap - thick, bw, thick),   // above
            (bx, by + bh + gap, bw, thick),      // below
            (bx - gap - thick, by, thick, bh),   // left
            (bx + bw + gap, by, thick, bh),      // right
        };
        var clipped = strips
            .Select(r =>
            {
                int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y);
                int x1 = Math.Min(imgW, r.X + r.W), y1 = Math.Min(imgH, r.Y + r.H);
                return (X: x0, Y: y0, W: x1 - x0, H: y1 - y0);
            })
            .Where(r => r.W > 0 && r.H > 0)
            .ToList();
        if (clipped.Count == 0) // the box covers the whole page: use its own outer edge instead
            clipped.Add((Math.Max(0, bx), Math.Max(0, by), Math.Min(imgW, bw), Math.Min(imgH, thick)));

        var tmp = Paths.NewTempDir();
        try
        {
            var samples = new List<byte[]>();
            for (int i = 0; i < clipped.Count; i++)
            {
                var r = clipped[i];
                var outFile = Path.Combine(tmp, $"s{i}.rgb");
                await ctx.Ff.RunAsync(new[]
                {
                    "-i", pagePng, "-vf", $"crop={r.W}:{r.H}:{r.X}:{r.Y},scale=1:1:flags=area",
                    "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", outFile,
                }, ctx.Token);
                var px = await File.ReadAllBytesAsync(outFile, ctx.Token);
                if (px.Length >= 3) samples.Add(px);
            }
            if (samples.Count == 0) return Rgb.White;

            byte Median(int channel)
            {
                var v = samples.Select(p => (int)p[channel]).OrderBy(c => c).ToList();
                return (byte)(v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2);
            }
            var color = new Rgb(Median(0), Median(1), Median(2));
            // near-white paper: snap to pure white so there's no faint visible patch
            return color.R >= 250 && color.G >= 250 && color.B >= 250 ? Rgb.White : color;
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    /// <summary>
    /// For "Replace text": reads the words inside a box on a rendered page, guesses the font size,
    /// and samples the background colour so the cover patch blends in.
    /// Box is in points; pagePng was rendered at dpi.
    /// </summary>
    public static async Task<(string Text, double FontSize, Rgb Background)> ReadRegionAsync(
        ActionContext ctx, string pagePng, double dpi, double x, double y, double w, double h, int viewRotation = 0)
    {
        // viewRotation: how the person sees the page (Studio rotation), so sideways text is turned upright for OCR
        string upright = (viewRotation % 360) switch
        {
            90 => ",transpose=1",
            180 => ",hflip,vflip",
            270 => ",transpose=2",
            _ => "",
        };
        double s = dpi / 72.0;
        int cx = Math.Max(0, (int)(x * s)), cy = Math.Max(0, (int)(y * s));
        int cw = Math.Max(4, (int)(w * s)), ch = Math.Max(4, (int)(h * s));
        var tmp = Paths.NewTempDir();
        try
        {
            var bg = await SampleBackgroundAsync(ctx, pagePng, dpi, x, y, w, h);

            string text = "";
            double fontSize = Math.Clamp(h * 0.7, 6, 72);
            if (ctx.OcrEngine != null)
            {
                var crop = Path.Combine(tmp, "crop.png");
                await ctx.Ff.RunAsync(new[] { "-i", pagePng, "-vf", $"crop={cw}:{ch}:{cx}:{cy},scale=iw*2:ih*2:flags=lanczos{upright}", "-frames:v", "1", "-update", "1", crop }, ctx.Token);
                var lines = await ctx.OcrEngine.RecognizeAsync(crop, ctx.Token);
                text = string.Join("\n", lines.Select(l => l.Text));
                var heights = lines.SelectMany(l => l.Words).Select(wd => wd.H).OrderBy(v => v).ToList();
                if (heights.Count > 0)
                    fontSize = Math.Clamp(heights[heights.Count / 2] / 2 / s / 0.78, 6, 72); // /2 undoes the upscale
            }
            return (text, Math.Round(fontSize * 2) / 2, bg);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }
}
