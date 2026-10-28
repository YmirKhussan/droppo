using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Droppo.Core.Studio;

/// <summary>
/// Just enough TrueType reading to embed an installed font in a PDF: names, metrics,
/// character widths for Western text, and the licence flag that says whether embedding is allowed.
/// </summary>
public sealed class TrueTypeFont
{
    public string Path { get; private init; } = "";
    public string Family { get; private set; } = "";
    public string Style { get; private set; } = "";
    public string PostScriptName { get; private set; } = "";
    public int UnitsPerEm { get; private set; } = 1000;
    public int Ascender { get; private set; }
    public int Descender { get; private set; }
    public int LineGap { get; private set; }
    public int CapHeight { get; private set; }
    public double ItalicAngle { get; private set; }
    public short[] BBox { get; private set; } = new short[4];
    public bool Embeddable { get; private set; } = true;

    /// <summary>Widths of WinAnsi codes 32..255 in 1/1000 em, the units PDF wants.</summary>
    public int[] Widths { get; private set; } = Array.Empty<int>();

    /// <summary>Line box measures as fractions of the font size (match what Windows shows on screen).</summary>
    public double AscentRatio => (double)Ascender / UnitsPerEm;
    public double LineRatio => (double)(Ascender - Descender + LineGap) / UnitsPerEm;

    private Dictionary<string, (uint Offset, uint Length)> _tables = new();

    /// <summary>Reads names and metrics. With widths=true also builds the width table (slower).</summary>
    public static TrueTypeFont Load(string path, bool widths)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var font = new TrueTypeFont { Path = path };
        var head = Read(fs, 0, 12);
        uint version = U32(head, 0);
        if (version != 0x00010000 && version != 0x74727565) // 'true'
            throw new InvalidDataException("Not a TrueType-outline font."); // CFF (.otf 'OTTO') and collections aren't supported
        int count = U16(head, 4);
        var dir = Read(fs, 12, count * 16);
        for (int i = 0; i < count; i++)
        {
            var tag = Encoding.ASCII.GetString(dir, i * 16, 4);
            font._tables[tag] = (U32(dir, i * 16 + 8), U32(dir, i * 16 + 12));
        }
        if (!font._tables.ContainsKey("glyf")) throw new InvalidDataException("Font has no TrueType outlines.");

        font.ReadNames(fs);
        font.ReadMetrics(fs);
        if (widths) font.ReadWidths(fs);
        return font;
    }

    private byte[] Table(Stream s, string tag)
    {
        if (!_tables.TryGetValue(tag, out var t)) throw new InvalidDataException($"Font has no {tag} table.");
        return Read(s, t.Offset, (int)t.Length);
    }

    private void ReadNames(Stream s)
    {
        var name = Table(s, "name");
        int count = U16(name, 2), strings = U16(name, 4);
        var found = new Dictionary<int, (int Score, string Value)>();
        for (int i = 0; i < count; i++)
        {
            int rec = 6 + i * 12;
            int platform = U16(name, rec), encoding = U16(name, rec + 2), language = U16(name, rec + 4), id = U16(name, rec + 6);
            int len = U16(name, rec + 8), off = U16(name, rec + 10);
            if (id is not (1 or 2 or 6 or 16 or 17) || strings + off + len > name.Length) continue;
            string value; int score;
            if (platform == 3 && encoding is 1 or 0) { value = Encoding.BigEndianUnicode.GetString(name, strings + off, len); score = language == 0x409 ? 3 : 2; }
            else if (platform == 1 && encoding == 0) { value = Encoding.Latin1.GetString(name, strings + off, len); score = 1; }
            else continue;
            if (!found.TryGetValue(id, out var cur) || score > cur.Score) found[id] = (score, value);
        }
        string Get(int id) => found.TryGetValue(id, out var v) ? v.Value.Trim() : "";
        Family = Get(16).Length > 0 ? Get(16) : Get(1);
        Style = Get(17).Length > 0 ? Get(17) : Get(2);
        var ps = Get(6).Length > 0 ? Get(6) : (Family + "-" + Style);
        PostScriptName = new string(ps.Where(c => c > 32 && c < 127 && "()<>[]{}/%#".IndexOf(c) < 0).ToArray());
        if (PostScriptName.Length == 0) PostScriptName = "EmbeddedFont";
    }

    private void ReadMetrics(Stream s)
    {
        var head = Table(s, "head");
        UnitsPerEm = Math.Max(16, U16(head, 18));
        BBox = new[] { I16(head, 36), I16(head, 38), I16(head, 40), I16(head, 42) };
        var hhea = Table(s, "hhea");
        Ascender = I16(hhea, 4);
        Descender = I16(hhea, 6);
        LineGap = I16(hhea, 8);
        CapHeight = (int)(Ascender * 0.7);
        if (_tables.ContainsKey("OS/2"))
        {
            var os2 = Table(s, "OS/2");
            int fsType = U16(os2, 8);
            Embeddable = (fsType & 0x000F) != 0x0002 && (fsType & 0x0200) == 0; // not "restricted", not bitmap-only
            if (U16(os2, 0) >= 2 && os2.Length >= 90) CapHeight = I16(os2, 88);
        }
        if (_tables.ContainsKey("post"))
        {
            var post = Table(s, "post");
            ItalicAngle = I16(post, 4) + U16(post, 6) / 65536.0;
        }
    }

    private void ReadWidths(Stream s)
    {
        var hhea = Table(s, "hhea");
        int metrics = U16(hhea, 34);
        var hmtx = Table(s, "hmtx");
        int Advance(int glyph) => U16(hmtx, Math.Min(glyph, metrics - 1) * 4);
        var cmap = BuildCmap(Table(s, "cmap"));
        Widths = new int[224];
        for (int code = 32; code < 256; code++)
        {
            char ch = WinAnsiToUnicode(code);
            int glyph = ch != '\0' && cmap.TryGetValue(ch, out var g) ? g : 0;
            Widths[code - 32] = (int)Math.Round(Advance(glyph) * 1000.0 / UnitsPerEm);
        }
    }

    /// <summary>Unicode to glyph from the Windows cmap (format 4, or format 12).</summary>
    private static Dictionary<int, int> BuildCmap(byte[] cmap)
    {
        int count = U16(cmap, 2);
        int best = -1, bestScore = 0;
        for (int i = 0; i < count; i++)
        {
            int platform = U16(cmap, 4 + i * 8), encoding = U16(cmap, 6 + i * 8);
            int off = (int)U32(cmap, 8 + i * 8);
            int format = U16(cmap, off);
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 4, (3, 1, 4) => 3, (0, _, 4) => 2, (0, _, 12) => 2, _ => 0,
            };
            if (score > bestScore) { bestScore = score; best = off; }
        }
        var map = new Dictionary<int, int>();
        if (best < 0) return map;
        if (U16(cmap, best) == 12)
        {
            uint groups = U32(cmap, best + 12);
            for (uint g = 0; g < groups; g++)
            {
                int b = best + 16 + (int)g * 12;
                uint start = U32(cmap, b), end = U32(cmap, b + 4), glyph = U32(cmap, b + 8);
                for (uint c = start; c <= end && c < 0x10000; c++) map[(int)c] = (int)(glyph + c - start);
            }
            return map;
        }
        int segs = U16(cmap, best + 6) / 2;
        int ends = best + 14, starts = ends + segs * 2 + 2, deltas = starts + segs * 2, ranges = deltas + segs * 2;
        for (int i = 0; i < segs; i++)
        {
            int end = U16(cmap, ends + i * 2), start = U16(cmap, starts + i * 2);
            short delta = I16(cmap, deltas + i * 2);
            int rangeOffset = U16(cmap, ranges + i * 2);
            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int glyph;
                if (rangeOffset == 0) glyph = (c + delta) & 0xFFFF;
                else
                {
                    int addr = ranges + i * 2 + rangeOffset + (c - start) * 2;
                    if (addr + 1 >= cmap.Length) continue;
                    glyph = U16(cmap, addr);
                    if (glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                }
                map[c] = glyph;
            }
        }
        return map;
    }

    private static readonly char[] Win80 =
    {
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    };

    public static char WinAnsiToUnicode(int code) => code switch
    {
        >= 0x80 and <= 0x9F => Win80[code - 0x80],
        _ => (char)code,
    };

    private static byte[] Read(Stream s, long offset, int length)
    {
        var buffer = new byte[length];
        s.Seek(offset, SeekOrigin.Begin);
        s.ReadExactly(buffer, 0, length);
        return buffer;
    }

    private static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    private static short I16(byte[] b, int i) => (short)((b[i] << 8) | b[i + 1]);
    private static uint U32(byte[] b, int i) => (uint)((b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3]);
}

/// <summary>Fonts on this PC that can be embedded (regular weights, TrueType outlines, licence allows it).</summary>
public static class FontCatalog
{
    public sealed record Entry(string Family, string Path);

    private static List<Entry>? _cache;
    private static readonly object Gate = new();

    public static IReadOnlyList<Entry> Installed()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            var dirs = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts));
                dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts"));
            }
            else
            {
                dirs.Add("/usr/share/fonts");
                dirs.Add("/usr/local/share/fonts");
            }

            var regular = new[] { "regular", "normal", "book", "roman", "" };
            var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in dirs.Where(Directory.Exists))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.ttf", SearchOption.AllDirectories).ToList(); }
                catch (Exception) { continue; }
                foreach (var file in files)
                {
                    try
                    {
                        var f = TrueTypeFont.Load(file, widths: false);
                        if (!f.Embeddable || f.Family.Length == 0) continue;
                        if (!regular.Contains(f.Style.Trim().ToLowerInvariant())) continue;
                        found.TryAdd(f.Family, new Entry(f.Family, file));
                    }
                    catch (Exception)
                    {
                        // unreadable or unsupported font file: skip
                    }
                }
            }
            _cache = found.Values.OrderBy(e => e.Family, StringComparer.OrdinalIgnoreCase).ToList();
            return _cache;
        }
    }
}
