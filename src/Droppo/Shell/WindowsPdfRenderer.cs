using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Droppo.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Droppo.Shell;

/// <summary>
/// Renders PDF pages with the PDF engine built into Windows 10/11 (the same one Edge uses).
/// No extra install needed.
/// </summary>
internal sealed class WindowsPdfRenderer : IPdfRenderer
{
    public async Task<IReadOnlyList<RenderedPage>> RenderAsync(string pdfPath, double dpi, string outDir, CancellationToken ct)
    {
        var doc = await OpenAsync(pdfPath);
        var pages = new List<RenderedPage>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(await RenderOneAsync(doc, i, dpi, Path.Combine(outDir, $"page{i + 1:D4}.png"), ct));
        }
        return pages;
    }

    public async Task<RenderedPage> RenderPageAsync(string pdfPath, int pageIndex, double dpi, string outPng, CancellationToken ct)
    {
        var doc = await OpenAsync(pdfPath);
        if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return await RenderOneAsync(doc, (uint)pageIndex, dpi, outPng, ct);
    }

    private static async Task<PdfDocument> OpenAsync(string pdfPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdfPath));
            return await PdfDocument.LoadFromFileAsync(file);
        }
        catch (Exception ex)
        {
            Log.Write($"Windows couldn't open {pdfPath}: {ex}");
            throw new ToolException("Windows couldn't open this PDF. It may be password-protected or damaged.");
        }
    }

    private static async Task<RenderedPage> RenderOneAsync(PdfDocument doc, uint index, double dpi, string outPath, CancellationToken ct)
    {
        using var page = doc.GetPage(index);
        double w = page.Size.Width, h = page.Size.Height; // 1/96 inch units, already accounts for page rotation
        double scale = dpi / 96.0;

        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(w * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(h * scale)),
        }; // default output is PNG on a white background

        using (var stream = new InMemoryRandomAccessStream())
        {
            await page.RenderToStreamAsync(stream, options);
            stream.Seek(0);
            using var input = stream.AsStreamForRead();
            using var output = File.Create(outPath);
            await input.CopyToAsync(output, ct);
        }
        return new RenderedPage(outPath, w * 72 / 96, h * 72 / 96);
    }
}
