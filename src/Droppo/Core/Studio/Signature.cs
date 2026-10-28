using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Droppo.Core.Studio;

/// <summary>Your signature, drawn once and saved in %AppData%\Droppo\signature.json.</summary>
public sealed class SavedSignature
{
    /// <summary>Strokes as [x, y] pairs, 0..1 inside a box of the given aspect (width / height).</summary>
    public List<List<double[]>> Strokes { get; set; } = new();
    public double Aspect { get; set; } = 3;

    public static string FilePath => Path.Combine(Settings.Folder, "signature.json");

    public static SavedSignature? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var sig = JsonSerializer.Deserialize<SavedSignature>(File.ReadAllText(FilePath));
            return sig is { Strokes.Count: > 0 } ? sig : null;
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't read saved signature: " + ex.Message);
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Settings.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    /// <summary>From raw pad strokes (any units): trims to the ink's own box with a little padding.</summary>
    public static SavedSignature FromStrokes(IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes)
    {
        var all = strokes.SelectMany(s => s).ToList();
        if (all.Count == 0) throw new ArgumentException("Draw your signature first.");
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double pad = Math.Max(maxX - minX, maxY - minY) * 0.04 + 1;
        minX -= pad; minY -= pad; maxX += pad; maxY += pad;
        double w = Math.Max(1, maxX - minX), h = Math.Max(1, maxY - minY);
        return new SavedSignature
        {
            Aspect = w / h,
            Strokes = strokes.Select(s => s.Select(p => new[] { (p.X - minX) / w, (p.Y - minY) / h }).ToList()).ToList(),
        };
    }

    /// <summary>A placeable copy at (x, y), widthPt wide, upright for the viewer.</summary>
    public SignatureEdit Place(double x, double y, double widthPt, int rotation, Rgb color) => new()
    {
        X = x, Y = y, W = widthPt, H = widthPt / Math.Max(0.2, Aspect), Rotation = rotation, Color = color,
        Strokes = Strokes.Select(s => s.Select(p => (p[0], p[1])).ToList()).ToList(),
    };
}
