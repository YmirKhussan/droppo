using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

public enum FitMode
{
    /// <summary>Fill the exact size, cutting off the edges that don't fit (centred).</summary>
    Crop,
    /// <summary>Whole picture inside the size, with white (or transparent) bars.</summary>
    Fit,
}

/// <summary>Either a percentage, or an exact pixel size with a fit mode.</summary>
public sealed record ResizeSpec(int Width, int Height, FitMode Mode, double? Percent = null, bool AsIcon = false)
{
    public string Suffix => AsIcon ? "" : Percent is double p ? $"-{Paths.Num(p)}%" : $"-{Width}x{Height}";
}

public static class ImageEditOps
{
    // ---------- resize / crop ----------

    public static WheelAction ResizeAction() => new()
    {
        Id = "img.resize", Label = "Resize", Hint = "exact size, crop", Prepare = AskResize, PerFile = Resize,
    };

    private static async Task<object?> AskResize(IReadOnlyList<string> files, ActionContext ctx)
    {
        (int W, int H)? first = null;
        try
        {
            var info = await ctx.Ff.ProbeAsync(files.First(File.Exists), ctx.Token);
            if (info.Width > 0) first = (info.Width, info.Height);
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't read image size: " + ex.Message);
        }
        return await ctx.Ask.AskResizeAsync(files.Count, first);
    }

    public static async Task<ActionOutput> Resize(string input, ActionContext ctx)
    {
        var spec = ctx.State as ResizeSpec ?? throw new InvalidOperationException("No size chosen.");
        if (spec.AsIcon) return await ImageOps.ToIco(input, ctx);

        var ext = Path.GetExtension(input).ToLowerInvariant();
        bool jpg = ext is ".jpg" or ".jpeg" or ".jfif" or ".heic" or ".heif" or ".avif";
        var outExt = jpg ? ".jpg" : ext is ".png" or ".webp" or ".bmp" ? ext : ".png";
        var output = Paths.NextTo(input, spec.Suffix, outExt);

        string filter;
        if (spec.Percent is double p)
        {
            var f = Paths.Num(p / 100.0);
            filter = $"scale=max(2\\,trunc(iw*{f})):max(2\\,trunc(ih*{f})):flags=lanczos";
        }
        else if (spec.Mode == FitMode.Crop)
        {
            filter = $"scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=increase:flags=lanczos,crop={spec.Width}:{spec.Height}";
        }
        else
        {
            // bars: white for JPG (no transparency), see-through for PNG/WebP
            var pad = jpg || outExt == ".bmp" ? "white" : "black@0";
            filter = $"scale={spec.Width}:{spec.Height}:force_original_aspect_ratio=decrease:flags=lanczos,format=rgba,pad={spec.Width}:{spec.Height}:(ow-iw)/2:(oh-ih)/2:color={pad}";
        }

        if (outExt == ".jpg")
        {
            await ImageOps.EncodeJpeg(ctx, input, output, 2, filter);
        }
        else
        {
            var args = new List<string> { "-i", input, "-vf", filter, "-frames:v", "1" };
            if (outExt == ".webp") args.AddRange(new[] { "-c:v", "libwebp", "-quality", "90" });
            else args.AddRange(new[] { "-update", "1" });
            args.Add(output);
            await ctx.Ff.RunAsync(args, ctx.Token);
        }
        return new(output);
    }

    // ---------- remove plain background ----------

    public static WheelAction RemoveBackgroundAction() => new()
    {
        Id = "img.nobg", Label = "No BG", Hint = "plain backgrounds", PerFile = RemoveBackground,
    };

    /// <summary>Pixels as RGBA, phone rotation applied.</summary>
    public static async Task<(int W, int H, byte[] Rgba)> ReadRgbaAsync(ActionContext ctx, string path)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var png = Path.Combine(tmp, "norm.png");
            await ctx.Ff.RunAsync(new[] { "-i", path, "-frames:v", "1", "-pix_fmt", "rgba", "-update", "1", png }, ctx.Token);
            var (w, h) = PngTools.ReadSize(png);
            var raw = Path.Combine(tmp, "pixels.rgba");
            await ctx.Ff.RunAsync(new[] { "-i", png, "-f", "rawvideo", "-pix_fmt", "rgba", raw }, ctx.Token);
            var rgba = await File.ReadAllBytesAsync(raw, ctx.Token);
            if (rgba.Length < w * h * 4) throw new InvalidDataException("Couldn't read the image pixels.");
            return (w, h, rgba);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    public static async Task WritePngAsync(ActionContext ctx, int w, int h, byte[] rgba, string output)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var raw = Path.Combine(tmp, "out.rgba");
            await File.WriteAllBytesAsync(raw, rgba, ctx.Token);
            await ctx.Ff.RunAsync(new[] { "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{w}x{h}", "-i", raw, "-frames:v", "1", "-update", "1", output }, ctx.Token);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    public static async Task<ActionOutput> RemoveBackground(string input, ActionContext ctx)
    {
        var (w, h, px) = await ReadRgbaAsync(ctx, input);
        var result = BackgroundRemover.Remove(w, h, px);
        var output = Paths.NextTo(input, "-nobg", ".png");
        await WritePngAsync(ctx, w, h, result.Rgba, output);
        return new(output, $"Removed {Math.Round(result.RemovedFraction * 100)}% of the picture as background");
    }
}

/// <summary>
/// Removes a plain background: finds the colour around the edges, flood-fills it inward, clears
/// matching holes (inside letters like O and A), and softens the cut edge without a colour halo.
/// No AI: great for logos, scans and product shots on plain colour, not for busy photos.
/// </summary>
public static class BackgroundRemover
{
    public sealed record Result(byte[] Rgba, double RemovedFraction);

    private const double Tolerance = 38, Soft = 75;

    public static Result Remove(int w, int h, byte[] src)
    {
        int n = w * h;
        var px = (byte[])src.Clone();

        // 1. background colour = median of the border pixels
        var border = new List<int>();
        for (int x = 0; x < w; x++) { border.Add(x); border.Add((h - 1) * w + x); }
        for (int y = 1; y < h - 1; y++) { border.Add(y * w); border.Add(y * w + w - 1); }
        byte Med(int c) => (byte)border.Select(i => (int)px[i * 4 + c]).OrderBy(v => v).ElementAt(border.Count / 2);
        int br = Med(0), bg = Med(1), bb = Med(2);

        var dist = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (px[i * 4 + 3] < 16) { dist[i] = 0; continue; } // already see-through
            double dr = px[i * 4] - br, dg = px[i * 4 + 1] - bg, db = px[i * 4 + 2] - bb;
            dist[i] = Math.Sqrt(dr * dr + dg * dg + db * db);
        }

        int plainEdges = border.Count(i => dist[i] <= Tolerance);
        if (plainEdges < border.Count * 0.6)
            throw new InvalidOperationException("The background isn't one plain colour, so it can't be removed cleanly. Works best on logos and product photos on a plain background.");

        // 2. flood fill from the edges
        var removed = new bool[n];
        var queue = new Queue<int>();
        foreach (var i in border.Where(i => dist[i] <= Tolerance && !removed[i])) { removed[i] = true; queue.Enqueue(i); }
        Flood(queue, removed, dist, w, h, Tolerance);

        // 3. enclosed holes of almost exactly the background colour (inside of letters)
        var seen = (bool[])removed.Clone();
        int minHole = Math.Max(4, n / 20000);
        for (int i = 0; i < n; i++)
        {
            if (seen[i] || dist[i] > Tolerance * 0.5) continue;
            var component = new List<int> { i };
            seen[i] = true;
            for (int k = 0; k < component.Count; k++)
            {
                int p = component[k], x = p % w, y = p / w;
                foreach (var q in Neighbours(x, y, w, h))
                    if (!seen[q] && dist[q] <= Tolerance * 0.5) { seen[q] = true; component.Add(q); }
            }
            if (component.Count >= minHole)
                foreach (var p in component) removed[p] = true;
        }

        // 4. clear removed pixels; soften the boundary and take the background tint out of edge pixels
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            if (removed[i]) { px[i * 4 + 3] = 0; count++; continue; }
            if (dist[i] >= Soft) continue;
            int x = i % w, y = i / w;
            if (!Neighbours(x, y, w, h).Any(q => removed[q])) continue;

            double a = Math.Clamp((dist[i] - Tolerance) / (Soft - Tolerance), 0.15, 1);
            px[i * 4 + 3] = (byte)Math.Round(px[i * 4 + 3] * a);
            px[i * 4] = Unblend(px[i * 4], br, a);
            px[i * 4 + 1] = Unblend(px[i * 4 + 1], bg, a);
            px[i * 4 + 2] = Unblend(px[i * 4 + 2], bb, a);
        }
        return new Result(px, (double)count / n);
    }

    private static byte Unblend(byte value, int background, double alpha) =>
        (byte)Math.Clamp(Math.Round((value - (1 - alpha) * background) / alpha), 0, 255);

    private static void Flood(Queue<int> queue, bool[] removed, double[] dist, int w, int h, double tol)
    {
        while (queue.Count > 0)
        {
            int p = queue.Dequeue(), x = p % w, y = p / w;
            foreach (var q in Neighbours(x, y, w, h))
            {
                if (removed[q] || dist[q] > tol) continue;
                removed[q] = true;
                queue.Enqueue(q);
            }
        }
    }

    private static IEnumerable<int> Neighbours(int x, int y, int w, int h)
    {
        if (x > 0) yield return y * w + x - 1;
        if (x < w - 1) yield return y * w + x + 1;
        if (y > 0) yield return (y - 1) * w + x;
        if (y < h - 1) yield return (y + 1) * w + x;
    }
}
