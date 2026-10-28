using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Droppo.Core.Studio;

/// <summary>A picture ready to embed: either JPEG bytes, or zlib-compressed RGB plus an optional alpha mask.</summary>
public sealed class PdfImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Data { get; init; }
    public required bool IsJpeg { get; init; }
    public byte[]? AlphaFlate { get; init; }
}

/// <summary>
/// Writes a PDF whose pages only contain the things we draw (text, boxes, pen strokes, images,
/// invisible OCR text). qpdf then lays these pages over the real ones, page for page.
/// Coordinates in: points, origin top-left. PDF wants bottom-left, so we flip here.
/// </summary>
public sealed class OverlayPdf
{
    // Arial (what the editor shows) and Helvetica (what the PDF uses) share the same widths.
    private const double Ascent = 0.905, LineGap = 1.149;

    private sealed class PageData
    {
        public double W, H;
        public readonly StringBuilder Ops = new();
    }

    private readonly List<PageData> _pages = new();
    private readonly List<PdfImage> _images = new();
    private readonly List<double> _alphas = new();
    private readonly List<(TrueTypeFont Font, byte[] Data)> _fonts = new();
    private readonly Dictionary<string, int> _fontIndex = new(StringComparer.OrdinalIgnoreCase);

    public int PageCount => _pages.Count;
    public bool HasContent => _pages.Any(p => p.Ops.Length > 0);

    public int AddPage(double widthPt, double heightPt)
    {
        _pages.Add(new PageData { W = widthPt, H = heightPt });
        return _pages.Count - 1;
    }

    public bool PageHasContent(int page) => _pages[page].Ops.Length > 0;

    // ---------- drawing ----------

    public void FillRect(int page, double x, double y, double w, double h, Rgb color, double alpha = 1)
    {
        var p = _pages[page];
        var o = p.Ops;
        o.Append("q ").Append(Alpha(alpha)).Append(Rgb(color)).Append(" rg ");
        o.Append($"{F(x)} {F(p.H - y - h)} {F(w)} {F(h)} re f Q\n");
    }

    /// <summary>
    /// Multi-line text whose box starts at (x, y), rotated clockwise around that corner.
    /// fontFile: a .ttf to embed (falls back to Helvetica if it can't be used).
    /// </summary>
    public void Text(int page, double x, double y, string text, double size, Rgb color, int rotationCw = 0, bool bold = false, double alpha = 1, string? fontFile = null)
    {
        var p = _pages[page];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string fontName = bold ? "/F2" : "/F1";
        double ascent = Ascent, lineGap = LineGap;
        if (fontFile != null && RegisterFont(fontFile) is int fi)
        {
            fontName = "/E" + fi;
            ascent = _fonts[fi].Font.AscentRatio;
            lineGap = _fonts[fi].Font.LineRatio;
        }
        var o = p.Ops;
        o.Append("q ").Append(Alpha(alpha)).Append(Matrix(rotationCw, x, p.H - y)).Append(' ');
        o.Append("BT ").Append(fontName).Append(' ').Append(F(size)).Append(" Tf ").Append(Rgb(color)).Append(" rg ");
        for (int i = 0; i < lines.Length; i++)
        {
            double baseline = -(ascent * size + i * lineGap * size);
            o.Append($"1 0 0 1 0 {F(baseline)} Tm ").Append(PdfString(lines[i])).Append(" Tj ");
        }
        o.Append("ET Q\n");
    }

    private int? RegisterFont(string path)
    {
        if (_fontIndex.TryGetValue(path, out var existing)) return existing >= 0 ? existing : null;
        try
        {
            var font = TrueTypeFont.Load(path, widths: true);
            if (!font.Embeddable) throw new InvalidOperationException("the font's licence doesn't allow embedding");
            _fonts.Add((font, File.ReadAllBytes(path)));
            _fontIndex[path] = _fonts.Count - 1;
            return _fonts.Count - 1;
        }
        catch (Exception ex)
        {
            Log.Write($"Font {path} can't be embedded ({ex.Message}); using Helvetica instead.");
            _fontIndex[path] = -1;
            return null;
        }
    }

    /// <summary>One line of text centred on (cx, cy), rotated clockwise (any angle). Used for watermarks.</summary>
    public void TextCentered(int page, double cx, double cy, string text, double size, Rgb color, double rotationCw, bool bold, double alpha)
    {
        var p = _pages[page];
        double w = TextWidth(text, size, bold);
        var o = p.Ops;
        o.Append("q ").Append(Alpha(alpha)).Append(Matrix(rotationCw, cx, p.H - cy)).Append(' ');
        o.Append("BT ").Append(bold ? "/F2 " : "/F1 ").Append(F(size)).Append(" Tf ").Append(Rgb(color)).Append(" rg ");
        o.Append($"1 0 0 1 {F(-w / 2)} {F(-size * 0.35)} Tm ").Append(PdfString(text)).Append(" Tj ET Q\n");
    }

    public void Ink(int page, IReadOnlyList<(double X, double Y)> points, double width, Rgb color)
    {
        if (points.Count == 0) return;
        var p = _pages[page];
        var o = p.Ops;
        o.Append($"q {F(width)} w 1 J 1 j ").Append(Rgb(color)).Append(" RG ");
        o.Append($"{F(points[0].X)} {F(p.H - points[0].Y)} m ");
        if (points.Count == 1) o.Append($"{F(points[0].X + 0.01)} {F(p.H - points[0].Y)} l ");
        for (int i = 1; i < points.Count; i++) o.Append($"{F(points[i].X)} {F(p.H - points[i].Y)} l ");
        o.Append("S Q\n");
    }

    public void Image(int page, double x, double y, double w, double h, PdfImage image, int rotationCw = 0)
    {
        int index = _images.IndexOf(image);
        if (index < 0) { _images.Add(image); index = _images.Count - 1; }
        var p = _pages[page];
        // In the rotated frame the image occupies (0, -h)..(w, 0)
        p.Ops.Append("q ").Append(Matrix(rotationCw, x, p.H - y)).Append($" {F(w)} 0 0 {F(h)} 0 {F(-h)} cm /Im{index} Do Q\n");
    }

    /// <summary>Invisible text stretched over a word's box, so scanned pages become searchable.</summary>
    public void InvisibleWord(int page, double x, double y, double w, double h, string word)
    {
        if (string.IsNullOrWhiteSpace(word) || w <= 0 || h <= 0) return;
        var p = _pages[page];
        double size = h * 0.85;
        double natural = TextWidth(word, size, false);
        double stretch = natural > 0 ? Math.Clamp(w / natural * 100, 10, 1000) : 100;
        p.Ops.Append($"BT 3 Tr /F1 {F(size)} Tf {F(stretch)} Tz 1 0 0 1 {F(x)} {F(p.H - y - h + h * 0.18)} Tm ")
             .Append(PdfString(word + " ")).Append(" Tj ET\n");
    }

    // ---------- helpers ----------

    public static double TextWidth(string text, double size, bool bold)
    {
        var table = bold ? FontMetrics.HelveticaBold : FontMetrics.Helvetica;
        double units = 0;
        foreach (var b in ToWinAnsi(text))
            units += b >= 32 ? table[b - 32] : 0;
        return units * size / 1000.0;
    }

    private string Alpha(double alpha)
    {
        if (alpha >= 0.999) return "";
        double a = Math.Round(Math.Clamp(alpha, 0, 1), 2);
        int i = _alphas.IndexOf(a);
        if (i < 0) { _alphas.Add(a); i = _alphas.Count - 1; }
        return $"/GS{i} gs ";
    }

    /// <summary>Translate to (x, y) then rotate clockwise by the given degrees.</summary>
    private static string Matrix(double rotationCw, double x, double y)
    {
        double r = rotationCw * Math.PI / 180;
        double cos = Math.Round(Math.Cos(r), 6), sin = Math.Round(Math.Sin(r), 6);
        return $"{F(cos)} {F(-sin)} {F(sin)} {F(cos)} {F(x)} {F(y)} cm";
    }

    private static string Rgb(Rgb c) => $"{F(c.R / 255.0)} {F(c.G / 255.0)} {F(c.B / 255.0)}";

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly Dictionary<char, byte> WinAnsiExtras = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86,
        ['‡'] = 0x87, ['ˆ'] = 0x88, ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C,
        ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93, ['”'] = 0x94, ['•'] = 0x95,
        ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>The built-in PDF fonts only know Western characters; anything else becomes "?".</summary>
    public static byte[] ToWinAnsi(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\t') bytes.Add(32);
            else if (ch >= 32 && ch < 127) bytes.Add((byte)ch);
            else if (ch >= 0xA0 && ch <= 0xFF) bytes.Add((byte)ch);
            else if (WinAnsiExtras.TryGetValue(ch, out var b)) bytes.Add(b);
            else if (ch >= 32) bytes.Add((byte)'?');
        }
        return bytes.ToArray();
    }

    private static string PdfString(string text)
    {
        var sb = new StringBuilder("(");
        foreach (var b in ToWinAnsi(text))
        {
            if (b == '(' || b == ')' || b == '\\') sb.Append('\\').Append((char)b);
            else if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // ---------- file output ----------

    public void Save(string outPath)
    {
        if (_pages.Count == 0) throw new InvalidOperationException("Overlay has no pages.");

        // Number every object up front so references can be written in one pass.
        int next = 1;
        int catalog = next++, pagesObj = next++, f1 = next++, f2 = next++;
        var gsObjs = _alphas.Select(_ => next++).ToList();
        var imageObjs = new List<(int Obj, int? Mask)>();
        foreach (var img in _images)
        {
            int obj = next++;
            int? mask = img.AlphaFlate != null ? next++ : null;
            imageObjs.Add((obj, mask));
        }
        var fontObjs = _fonts.Select(_ => (Dict: next++, Descriptor: next++, File: next++)).ToList();
        var pageObjs = _pages.Select(_ => (Page: next++, Content: next++)).ToList();
        int total = next - 1;

        var offsets = new long[total + 1];
        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        void Raw(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }
        void Begin(int n) { offsets[n] = fs.Position; Raw($"{n} 0 obj\n"); }
        void Stream(int n, string dict, byte[] data)
        {
            Begin(n);
            Raw($"<< {dict} /Length {data.Length} >>\nstream\n");
            fs.Write(data, 0, data.Length);
            Raw("\nendstream\nendobj\n");
        }

        Raw("%PDF-1.4\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

        Begin(catalog);
        Raw($"<< /Type /Catalog /Pages {pagesObj} 0 R >>\nendobj\n");
        Begin(pagesObj);
        Raw($"<< /Type /Pages /Count {_pages.Count} /Kids [ {string.Join(" ", pageObjs.Select(p => $"{p.Page} 0 R"))} ] >>\nendobj\n");
        Begin(f1);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        Begin(f2);
        Raw("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        for (int i = 0; i < _fonts.Count; i++)
        {
            var (font, data) = _fonts[i];
            var (dict, descriptor, file) = fontObjs[i];
            double k = 1000.0 / font.UnitsPerEm;
            string name = _fonts.Take(i).Any(f => f.Font.PostScriptName == font.PostScriptName) ? $"{font.PostScriptName}-{i}" : font.PostScriptName;
            Begin(dict);
            Raw($"<< /Type /Font /Subtype /TrueType /BaseFont /{name} /FirstChar 32 /LastChar 255 " +
                $"/Widths [{string.Join(" ", font.Widths)}] /FontDescriptor {descriptor} 0 R /Encoding /WinAnsiEncoding >>\nendobj\n");
            Begin(descriptor);
            Raw($"<< /Type /FontDescriptor /FontName /{name} /Flags 32 " +
                $"/FontBBox [{F(font.BBox[0] * k)} {F(font.BBox[1] * k)} {F(font.BBox[2] * k)} {F(font.BBox[3] * k)}] " +
                $"/ItalicAngle {F(font.ItalicAngle)} /Ascent {F(font.Ascender * k)} /Descent {F(font.Descender * k)} " +
                $"/CapHeight {F(font.CapHeight * k)} /StemV 80 /FontFile2 {file} 0 R >>\nendobj\n");
            Stream(file, $"/Length1 {data.Length} /Filter /FlateDecode", Deflate(data));
        }

        for (int i = 0; i < _alphas.Count; i++)
        {
            Begin(gsObjs[i]);
            Raw($"<< /Type /ExtGState /ca {F(_alphas[i])} /CA {F(_alphas[i])} >>\nendobj\n");
        }

        for (int i = 0; i < _images.Count; i++)
        {
            var img = _images[i];
            var (obj, mask) = imageObjs[i];
            var common = $"/Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /BitsPerComponent 8";
            if (img.IsJpeg)
            {
                var comps = JpegTools.ReadInfo(img.Data).Components;
                var cs = comps == 1 ? "/DeviceGray" : comps == 4 ? "/DeviceCMYK /Decode [1 0 1 0 1 0 1 0]" : "/DeviceRGB";
                Stream(obj, $"{common} /ColorSpace {cs} /Filter /DCTDecode" + (mask.HasValue ? $" /SMask {mask} 0 R" : ""), img.Data);
            }
            else
            {
                Stream(obj, $"{common} /ColorSpace /DeviceRGB /Filter /FlateDecode" + (mask.HasValue ? $" /SMask {mask} 0 R" : ""), img.Data);
            }
            if (mask.HasValue)
                Stream(mask.Value, $"/Type /XObject /Subtype /Image /Width {img.Width} /Height {img.Height} /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", img.AlphaFlate!);
        }

        var gsRes = _alphas.Count == 0 ? "" : "/ExtGState << " + string.Join(" ", gsObjs.Select((o, i) => $"/GS{i} {o} 0 R")) + " >> ";
        var xRes = _images.Count == 0 ? "" : "/XObject << " + string.Join(" ", imageObjs.Select((o, i) => $"/Im{i} {o.Obj} 0 R")) + " >> ";
        var embedded = string.Concat(fontObjs.Select((o, i) => $" /E{i} {o.Dict} 0 R"));
        var resources = $"<< /Font << /F1 {f1} 0 R /F2 {f2} 0 R{embedded} >> {gsRes}{xRes}>>";

        for (int i = 0; i < _pages.Count; i++)
        {
            var page = _pages[i];
            var (pageObj, contentObj) = pageObjs[i];
            Begin(pageObj);
            Raw($"<< /Type /Page /Parent {pagesObj} 0 R /MediaBox [0 0 {F(page.W)} {F(page.H)}] /Resources {resources} /Contents {contentObj} 0 R >>\nendobj\n");
            Stream(contentObj, "/Filter /FlateDecode", Deflate(Encoding.ASCII.GetBytes(page.Ops.ToString())));
        }

        long xref = fs.Position;
        Raw($"xref\n0 {total + 1}\n0000000000 65535 f \n");
        for (int n = 1; n <= total; n++) Raw($"{offsets[n]:D10} 00000 n \n");
        Raw($"trailer\n<< /Size {total + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    // ---------- loading pictures ----------

    /// <summary>Any image file to an embeddable picture (phone rotation applied, transparency kept).</summary>
    public static async System.Threading.Tasks.Task<PdfImage> LoadImageAsync(ActionContext ctx, string path)
    {
        var tmp = Paths.NewTempDir();
        try
        {
            var png = Path.Combine(tmp, "norm.png");
            await ctx.Ff.RunAsync(new[] { "-i", path, "-frames:v", "1", "-pix_fmt", "rgba", "-update", "1", png }, ctx.Token);
            var head = new byte[24];
            using (var f = File.OpenRead(png)) f.ReadExactly(head, 0, 24);
            int w = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            int h = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];

            var raw = Path.Combine(tmp, "pixels.rgba");
            await ctx.Ff.RunAsync(new[] { "-i", png, "-f", "rawvideo", "-pix_fmt", "rgba", raw }, ctx.Token);
            var rgba = await File.ReadAllBytesAsync(raw, ctx.Token);
            if (rgba.Length < w * h * 4) throw new InvalidDataException("Couldn't read the image pixels.");

            bool transparent = false;
            for (int i = 3; i < w * h * 4; i += 4) if (rgba[i] != 255) { transparent = true; break; }

            if (!transparent)
            {
                var jpg = Path.Combine(tmp, "img.jpg");
                await ImageOps.EncodeJpeg(ctx, png, jpg, 2, null);
                return new PdfImage { Width = w, Height = h, Data = await File.ReadAllBytesAsync(jpg, ctx.Token), IsJpeg = true };
            }

            var rgb = new byte[w * h * 3];
            var alpha = new byte[w * h];
            for (int i = 0, j = 0, k = 0; i < w * h; i++, j += 4, k += 3)
            {
                rgb[k] = rgba[j]; rgb[k + 1] = rgba[j + 1]; rgb[k + 2] = rgba[j + 2];
                alpha[i] = rgba[j + 3];
            }
            return new PdfImage { Width = w, Height = h, Data = Deflate(rgb), IsJpeg = false, AlphaFlate = Deflate(alpha) };
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }
}
