using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Droppo.Core;

/// <summary>
/// Minimal PDF writer: one JPEG per page. JPEG bytes go in untouched (PDF understands JPEG
/// natively), so it's fast and small.
/// </summary>
public static class PdfWriter
{
    private const double A4W = 595.28, A4H = 841.89, Margin = 28;

    /// <summary>One page: the JPEG, the page size in points, and where the image sits on it.</summary>
    public sealed record Page(byte[] Jpeg, double PageW, double PageH, double X, double Y, double DrawW, double DrawH);

    /// <summary>Each image on its own A4 page (landscape for wide images), fitted with a small margin.</summary>
    public static void WriteImagesToA4(IReadOnlyList<byte[]> jpegs, string outPath)
    {
        var pages = jpegs.Select(jpg =>
        {
            var info = JpegTools.ReadInfo(jpg);
            bool landscape = info.Width > info.Height;
            double pw = landscape ? A4H : A4W, ph = landscape ? A4W : A4H;
            double scale = Math.Min((pw - 2 * Margin) / info.Width, (ph - 2 * Margin) / info.Height);
            double dw = info.Width * scale, dh = info.Height * scale;
            return new Page(jpg, pw, ph, (pw - dw) / 2, (ph - dh) / 2, dw, dh);
        }).ToList();
        Write(pages, outPath);
    }

    /// <summary>Each image fills a page of the given size (used to rebuild a PDF from rendered pages).</summary>
    public static void WriteFullPages(IReadOnlyList<(byte[] Jpeg, double WidthPt, double HeightPt)> pages, string outPath) =>
        Write(pages.Select(p => new Page(p.Jpeg, p.WidthPt, p.HeightPt, 0, 0, p.WidthPt, p.HeightPt)).ToList(), outPath);

    public static void Write(IReadOnlyList<Page> pages, string outPath)
    {
        if (pages.Count == 0) throw new ArgumentException("No pages to put in the PDF.");

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        var offsets = new List<long>();
        int objCount = 2 + pages.Count * 3; // catalog, pages, then (page, content, image) per page

        void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void BeginObj(int n) { offsets.Add(fs.Position); Raw($"{n} 0 obj\n"); }
        static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        Raw("%PDF-1.4\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6); // marks the file as binary

        BeginObj(1);
        Raw("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = new StringBuilder();
        for (int p = 0; p < pages.Count; p++) kids.Append($"{3 + p * 3} 0 R ");
        BeginObj(2);
        Raw($"<< /Type /Pages /Count {pages.Count} /Kids [ {kids}] >>\nendobj\n");

        for (int p = 0; p < pages.Count; p++)
        {
            int pageObj = 3 + p * 3, contentObj = pageObj + 1, imageObj = pageObj + 2;
            var page = pages[p];
            var info = JpegTools.ReadInfo(page.Jpeg);

            BeginObj(pageObj);
            Raw($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(page.PageW)} {F(page.PageH)}] " +
                $"/Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

            var content = $"q {F(page.DrawW)} 0 0 {F(page.DrawH)} {F(page.X)} {F(page.Y)} cm /Im0 Do Q\n";
            BeginObj(contentObj);
            Raw($"<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

            string colorSpace = info.Components switch
            {
                1 => "/DeviceGray",
                4 => "/DeviceCMYK /Decode [1 0 1 0 1 0 1 0]",
                _ => "/DeviceRGB",
            };
            BeginObj(imageObj);
            Raw($"<< /Type /XObject /Subtype /Image /Width {info.Width} /Height {info.Height} " +
                $"/ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
            fs.Write(page.Jpeg, 0, page.Jpeg.Length);
            Raw("\nendstream\nendobj\n");
        }

        long xref = fs.Position;
        Raw($"xref\n0 {objCount + 1}\n0000000000 65535 f \n");
        foreach (var off in offsets) Raw($"{off:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
