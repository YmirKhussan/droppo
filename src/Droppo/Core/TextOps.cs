using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>Copy text out of images and PDFs using OCR (works on scans and photos too).</summary>
public static class TextOps
{
    public const double PdfOcrDpi = 200;

    public static async Task<ActionOutput> FromPdf(string input, ActionContext ctx)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var pages = await ctx.Renderer.RenderAsync(input, PdfOcrDpi, tmp, ctx.Token);
            var sb = new StringBuilder();
            foreach (var page in pages)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var lines = await ctx.Ocr.RecognizeAsync(page.PngPath, ctx.Token);
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(string.Join("\n", lines.Select(l => l.Text)));
            }
            return Finish(input, sb.ToString(), pages.Count);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    public static async Task<ActionOutput> FromImage(string input, ActionContext ctx)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            // Normalise to PNG (handles HEIC/WebP and phone rotation) and enlarge small images, OCR likes big text
            var png = Path.Combine(tmp, "page.png");
            await ctx.Ff.RunAsync(new[]
            {
                "-i", input, "-frames:v", "1", "-vf", "scale='if(lt(iw,1400),iw*2,iw)':-1:flags=lanczos", "-update", "1", png,
            }, ctx.Token);
            var lines = await ctx.Ocr.RecognizeAsync(png, ctx.Token);
            return Finish(input, string.Join("\n", lines.Select(l => l.Text)), 1);
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    private static ActionOutput Finish(string input, string text, int pages)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(null, $"No text found in {Path.GetFileName(input)}");
        var txt = Paths.NextTo(input, "", ".txt");
        File.WriteAllText(txt, text);
        var words = text.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries).Length;
        return new(txt, $"{words} words copied{(pages > 1 ? $" from {pages} pages" : "")}, also saved as {Path.GetFileName(txt)}", text);
    }
}
