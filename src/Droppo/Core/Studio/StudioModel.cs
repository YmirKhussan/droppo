using System;
using System.Collections.Generic;
using System.Linq;

namespace Droppo.Core.Studio;

/// <summary>A colour for edits. Kept simple: RGB bytes.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0x11, 0x11, 0x11);
    public static readonly Rgb Blue = new(0x1A, 0x4F, 0xD6);
    public static readonly Rgb Red = new(0xD0, 0x24, 0x24);
    public static readonly Rgb White = new(0xFF, 0xFF, 0xFF);
    public static readonly Rgb Yellow = new(0xFF, 0xE0, 0x3D);
    public static readonly Rgb Gray = new(0x55, 0x55, 0x55);
}

/// <summary>
/// Something drawn on top of a page. Coordinates are PDF points (1/72 inch), origin at the
/// page's top-left, in the page's own orientation (before any rotation the user adds in Studio).
/// </summary>
public abstract class PageEdit
{
    public abstract PageEdit Clone();
}

/// <summary>Free text. (X, Y) is the top-left of the text box. Rotation is clockwise degrees around that corner.</summary>
public sealed class TextEdit : PageEdit
{
    public double X { get; set; }
    public double Y { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 12;
    public Rgb Color { get; set; } = Rgb.Black;
    public int Rotation { get; set; }

    /// <summary>A .ttf from your PC to embed; null = the built-in Helvetica (looks like Arial).</summary>
    public string? FontFile { get; set; }

    /// <summary>Family name for showing it on screen, e.g. "Calibri".</summary>
    public string? FontFamily { get; set; }

    public override PageEdit Clone() => (TextEdit)MemberwiseClone();
}

/// <summary>Filled rectangle: white-out (opaque) or highlight (see-through yellow).</summary>
public sealed class RectEdit : PageEdit
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public Rgb Color { get; set; } = Rgb.White;
    public double Opacity { get; set; } = 1;

    public override PageEdit Clone() => (RectEdit)MemberwiseClone();
}

/// <summary>Freehand pen stroke (signatures, ticks, circles).</summary>
public sealed class InkEdit : PageEdit
{
    public List<(double X, double Y)> Points { get; set; } = new();
    public double Width { get; set; } = 1.6;
    public Rgb Color { get; set; } = Rgb.Black;

    public override PageEdit Clone() => new InkEdit { Points = Points.ToList(), Width = Width, Color = Color };
}

/// <summary>
/// Your saved signature, placed as a group. Strokes are stored 0..1 inside the signature's own box,
/// so it scales cleanly. (X, Y) is the box's top-left; rotation is clockwise around that corner.
/// </summary>
public sealed class SignatureEdit : PageEdit
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public List<List<(double X, double Y)>> Strokes { get; set; } = new();
    public Rgb Color { get; set; } = Rgb.Blue;
    public int Rotation { get; set; }

    public double PenWidth => Math.Max(0.8, W / 110);

    /// <summary>Strokes in page coordinates (rotation applied).</summary>
    public IEnumerable<List<(double X, double Y)>> PageStrokes()
    {
        double rad = Rotation * Math.PI / 180, cos = Math.Cos(rad), sin = Math.Sin(rad);
        foreach (var stroke in Strokes)
            yield return stroke.Select(p =>
            {
                double lx = p.X * W, ly = p.Y * H;
                return (X + lx * cos - ly * sin, Y + lx * sin + ly * cos);
            }).ToList();
    }

    public override PageEdit Clone() => new SignatureEdit
    {
        X = X, Y = Y, W = W, H = H, Color = Color, Rotation = Rotation,
        Strokes = Strokes.Select(s => s.ToList()).ToList(),
    };
}

/// <summary>A picture placed on the page (logo, stamp, signature photo). Transparency is kept.</summary>
public sealed class ImageEdit : PageEdit
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public string ImagePath { get; set; } = "";
    public int Rotation { get; set; }

    public override PageEdit Clone() => (ImageEdit)MemberwiseClone();
}

/// <summary>One page in Studio: where it comes from, how it's turned, what's drawn on it.</summary>
public sealed class StudioPage
{
    public required string SourcePdf { get; init; }

    /// <summary>0-based page index inside <see cref="SourcePdf"/>.</summary>
    public required int SourceIndex { get; init; }

    /// <summary>Page size in points as it normally displays (before Studio rotation).</summary>
    public required double WidthPt { get; init; }
    public required double HeightPt { get; init; }

    /// <summary>Name shown in the UI, e.g. the original file name.</summary>
    public string SourceName { get; init; } = "";

    /// <summary>The file the person actually dropped (SourcePdf may be a converted/unlocked temp copy).</summary>
    public string OriginalPath { get; init; } = "";

    /// <summary>Small preview image (PNG) for the page grid.</summary>
    public string? ThumbPath { get; set; }

    /// <summary>Extra clockwise rotation added in Studio: 0, 90, 180 or 270.</summary>
    public int UserRotation { get; set; }

    public List<PageEdit> Edits { get; set; } = new();

    /// <summary>Fillable form fields on this page (empty for normal pages).</summary>
    public List<FormField> Fields { get; set; } = new();

    public double DisplayWidth => UserRotation % 180 == 0 ? WidthPt : HeightPt;
    public double DisplayHeight => UserRotation % 180 == 0 ? HeightPt : WidthPt;

    public void Rotate(int degreesClockwise) => UserRotation = ((UserRotation + degreesClockwise) % 360 + 360) % 360;

    public StudioPage CloneShallowEdits() => new()
    {
        SourcePdf = SourcePdf, SourceIndex = SourceIndex, WidthPt = WidthPt, HeightPt = HeightPt,
        SourceName = SourceName, OriginalPath = OriginalPath, ThumbPath = ThumbPath, UserRotation = UserRotation,
        Edits = Edits.Select(e => e.Clone()).ToList(),
        Fields = Fields.Select(f => f.Clone()).ToList(),
    };
}

public enum NumberPosition { BottomCenter, BottomRight, TopRight }

public sealed class PageNumberOptions
{
    public NumberPosition Position { get; set; } = NumberPosition.BottomCenter;

    /// <summary>{n} = page number, {total} = page count.</summary>
    public string Format { get; set; } = "{n}";
    public int StartAt { get; set; } = 1;
    public double FontSize { get; set; } = 10;
}

public sealed class WatermarkOptions
{
    public string Text { get; set; } = "DRAFT";
    public double Opacity { get; set; } = 0.15;
    public bool Diagonal { get; set; } = true;
    public Rgb Color { get; set; } = Rgb.Gray;
}

/// <summary>Everything the user set up in Studio. Nothing touches the original files until export.</summary>
public sealed class StudioDocument
{
    public List<StudioPage> Pages { get; } = new();
    public PageNumberOptions? PageNumbers { get; set; }
    public WatermarkOptions? Watermark { get; set; }

    /// <summary>When set, the saved PDF needs this password to open.</summary>
    public string? Password { get; set; }

    /// <summary>Run OCR and add an invisible text layer so scanned pages become searchable/copyable.</summary>
    public bool MakeSearchable { get; set; }
}
