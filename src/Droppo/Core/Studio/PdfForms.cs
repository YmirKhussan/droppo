using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core.Studio;

public enum FieldKind { Text, Checkbox, Radio, Choice }

/// <summary>
/// One fillable box in a PDF form. Position is in points, top-left origin, as the page displays
/// (same space as Studio edits). For radio buttons, each button is its own entry sharing a Name.
/// </summary>
public sealed class FormField
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public required int PageIndex { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public bool Multiline { get; init; }
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>Text for text/choice fields; "1" or "" for checkboxes and radio buttons.</summary>
    public string Value { get; set; } = "";
    public string Original { get; init; } = "";

    public bool Changed => Value != Original;

    public FormField Clone() => (FormField)MemberwiseClone();
}

public static class PdfForms
{
    /// <summary>Reads every fillable field (text, checkbox, radio, dropdown) and where it sits on its page.</summary>
    public static async Task<List<FormField>> ReadAsync(Qpdf q, string pdf, CancellationToken ct)
    {
        string json;
        try
        {
            json = await q.RunAsync(new[] { "--json=2", "--json-key=acroform", "--json-key=pages", "--json-key=qpdf", pdf }, ct).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            Log.Write("Couldn't read form fields: " + ex.Message);
            return new List<FormField>();
        }
        return Parse(json);
    }

    public static List<FormField> Parse(string json)
    {
        var fields = new List<FormField>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("acroform", out var acro) || !acro.TryGetProperty("hasacroform", out var has) || !has.GetBoolean())
            return fields;
        var objects = root.GetProperty("qpdf")[1];
        var pages = root.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("object").GetString() ?? "").ToList();

        JsonElement? Obj(string? reference) =>
            reference != null && objects.TryGetProperty("obj:" + reference, out var o) && o.TryGetProperty("value", out var v) ? v : null;

        JsonElement? Resolve(JsonElement e) =>
            e.ValueKind == JsonValueKind.String && e.GetString() is { } s && s.EndsWith(" R") ? Obj(s) : e;

        JsonElement? Inherited(JsonElement page, string key, int depth = 0)
        {
            if (depth > 30 || page.ValueKind != JsonValueKind.Object) return null;
            if (page.TryGetProperty(key, out var v)) return Resolve(v);
            return page.TryGetProperty("/Parent", out var parent) && Resolve(parent) is { } p ? Inherited(p, key, depth + 1) : null;
        }

        static double[]? Numbers(JsonElement? e) =>
            e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray() : null;

        foreach (var f in acro.GetProperty("fields").EnumerateArray())
        {
            try
            {
                FieldKind kind;
                if (f.GetProperty("istext").GetBoolean()) kind = FieldKind.Text;
                else if (f.GetProperty("ischeckbox").GetBoolean()) kind = FieldKind.Checkbox;
                else if (f.GetProperty("isradiobutton").GetBoolean()) kind = FieldKind.Radio;
                else if (f.GetProperty("ischoice").GetBoolean()) kind = FieldKind.Choice;
                else continue; // push buttons, signature fields

                int pageIndex = f.GetProperty("pageposfrom1").GetInt32() - 1;
                if (pageIndex < 0 || pageIndex >= pages.Count) continue;
                var annotation = Obj(f.GetProperty("annotation").GetProperty("object").GetString());
                var page = Obj(pages[pageIndex]);
                if (annotation == null || page == null) continue;

                var rect = Numbers(annotation.Value.TryGetProperty("/Rect", out var r) ? Resolve(r) : null);
                var box = Numbers(Inherited(page.Value, "/CropBox")) ?? Numbers(Inherited(page.Value, "/MediaBox"));
                if (rect is not { Length: 4 } || box is not { Length: 4 }) continue;
                int rotate = Inherited(page.Value, "/Rotate") is { ValueKind: JsonValueKind.Number } rot ? rot.GetInt32() : 0;
                var (x, y, w, h) = ToDisplay(rect, box, rotate);

                string name = f.GetProperty("fullname").GetString() ?? "";
                string label = f.TryGetProperty("alternativename", out var alt) && alt.GetString() is { Length: > 0 } a ? a : name;
                int flags = f.TryGetProperty("fieldflags", out var ff) && ff.ValueKind == JsonValueKind.Number ? ff.GetInt32() : 0;

                string value = "";
                if (kind == FieldKind.Radio)
                {
                    // this button's "on" name vs the group's current value
                    string? onState = annotation.Value.TryGetProperty("/AP", out var ap) && Resolve(ap) is { } apd
                        && apd.TryGetProperty("/N", out var nn) && Resolve(nn) is { ValueKind: JsonValueKind.Object } normal
                        ? normal.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => n != "/Off") : null;
                    var parent = f.TryGetProperty("parent", out var pr) ? Obj(pr.GetString()) : null;
                    string? group = parent is { } pd && pd.TryGetProperty("/V", out var gv) ? gv.GetString() : null;
                    value = onState != null && group == onState ? "1" : "";
                }
                else if (kind == FieldKind.Checkbox)
                {
                    var v = f.TryGetProperty("value", out var cv) ? cv.GetString() : null;
                    value = v != null && v != "/Off" ? "1" : "";
                }
                else
                {
                    value = Text(f.TryGetProperty("value", out var tv) ? tv : default);
                }

                var choices = f.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array
                    ? ch.EnumerateArray().Select(Text).ToList() : new List<string>();

                fields.Add(new FormField
                {
                    Name = name, Label = label, Kind = kind, PageIndex = pageIndex, X = x, Y = y, W = w, H = h,
                    Multiline = kind == FieldKind.Text && (flags & 4096) != 0, Choices = choices, Value = value, Original = value,
                });
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Log.Write("Skipped an unusual form field: " + ex.Message);
            }
        }
        return fields;
    }

    /// <summary>qpdf's JSON text: "u:hello" (text), "/Name" (names), "b:..." (bytes, rare).</summary>
    private static string Text(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) return "";
        var s = e.GetString() ?? "";
        if (s.StartsWith("u:", StringComparison.Ordinal)) return s[2..];
        if (s.StartsWith("/", StringComparison.Ordinal)) return s[1..];
        return "";
    }

    /// <summary>PDF rectangle (bottom-left origin, unrotated page) to top-left points as the page displays.</summary>
    public static (double X, double Y, double W, double H) ToDisplay(double[] rect, double[] box, int rotate)
    {
        double bx0 = Math.Min(box[0], box[2]), by0 = Math.Min(box[1], box[3]);
        double bx1 = Math.Max(box[0], box[2]), by1 = Math.Max(box[1], box[3]);
        double bw = bx1 - bx0, bh = by1 - by0;
        var corners = new[] { (rect[0], rect[1]), (rect[2], rect[3]) }.Select(c =>
        {
            double u = c.Item1 - bx0, v = by1 - c.Item2;
            return (((rotate % 360) + 360) % 360) switch
            {
                90 => (bh - v, u),
                180 => (bw - u, bh - v),
                270 => (v, bw - u),
                _ => (u, v),
            };
        }).ToList();
        double x0 = Math.Min(corners[0].Item1, corners[1].Item1), y0 = Math.Min(corners[0].Item2, corners[1].Item2);
        double x1 = Math.Max(corners[0].Item1, corners[1].Item1), y1 = Math.Max(corners[0].Item2, corners[1].Item2);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Draws a changed answer onto the page overlay (the PDF's own form is flattened first).</summary>
    public static void Draw(OverlayPdf overlay, int page, FormField f)
    {
        bool hadSomething = !string.IsNullOrEmpty(f.Original);
        if (hadSomething) // hide the old answer, keep the box's border
            overlay.FillRect(page, f.X + 1.2, f.Y + 1.2, Math.Max(0, f.W - 2.4), Math.Max(0, f.H - 2.4), Rgb.White);

        switch (f.Kind)
        {
            case FieldKind.Checkbox when f.Value == "1":
                overlay.Ink(page, new List<(double, double)>
                {
                    (f.X + 0.2 * f.W, f.Y + 0.55 * f.H), (f.X + 0.42 * f.W, f.Y + 0.78 * f.H), (f.X + 0.82 * f.W, f.Y + 0.22 * f.H),
                }, Math.Max(1, f.H * 0.12), Rgb.Black);
                break;
            case FieldKind.Radio when f.Value == "1":
                overlay.Ink(page, new List<(double, double)> { (f.X + f.W / 2, f.Y + f.H / 2) }, Math.Min(f.W, f.H) * 0.45, Rgb.Black);
                break;
            case FieldKind.Text or FieldKind.Choice when !string.IsNullOrWhiteSpace(f.Value):
                var (text, size, top) = Layout(f);
                overlay.Text(page, f.X + 2, top, text, size, Rgb.Black);
                break;
        }
    }

    /// <summary>Single line: shrink to fit. Multi-line: wrap to the box width.</summary>
    public static (string Text, double Size, double Top) Layout(FormField f)
    {
        double inner = Math.Max(4, f.W - 4);
        if (f.Multiline || f.H > 34)
        {
            double size = Math.Min(10, Math.Max(6, f.H * 0.3));
            return (Wrap(f.Value, size, inner), size, f.Y + 2);
        }
        double s = Math.Min(12, Math.Max(5, f.H * 0.68));
        while (s > 5 && OverlayPdf.TextWidth(f.Value, s, false) > inner) s -= 0.5;
        return (f.Value.Replace("\r", "").Replace("\n", " "), s, f.Y + (f.H - s * 1.149) / 2);
    }

    private static string Wrap(string text, double size, double width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && OverlayPdf.TextWidth(candidate, size, false) > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else line = candidate;
            }
            lines.Add(line);
        }
        return string.Join("\n", lines);
    }

    public static string Describe(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
