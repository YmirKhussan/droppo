using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Droppo.Core;

public static class Paths
{
    /// <summary>
    /// Output file next to the input: photo.heic + ("", ".jpg") gives photo.jpg,
    /// photo.jpg + ("-clean", ".jpg") gives photo-clean.jpg. Never overwrites: adds " (1)", " (2)"...
    /// </summary>
    private static readonly System.Threading.AsyncLocal<string?> Override = new();

    /// <summary>
    /// When set (Settings: "save to a folder" or "ask"), results go here instead of next to the original.
    /// Scoped to the running action, so two actions at once don't mix up their folders.
    /// </summary>
    public static string? OutputOverride
    {
        get => Override.Value;
        set => Override.Value = value;
    }

    /// <summary>Where results for this input should be saved.</summary>
    public static string OutputFolderFor(string input)
    {
        var over = OutputOverride;
        if (!string.IsNullOrWhiteSpace(over))
        {
            Directory.CreateDirectory(over);
            return over;
        }
        return Path.GetDirectoryName(Path.GetFullPath(input.TrimEnd('\\', '/'))) ?? ".";
    }

    public static string NextTo(string input, string suffix, string ext)
    {
        var dir = OutputFolderFor(input);
        var stem = Path.GetFileNameWithoutExtension(input);
        return Unique(dir, stem + suffix, ext);
    }

    public static string Unique(string dir, string stem, string ext)
    {
        if (!ext.StartsWith('.')) ext = "." + ext;
        var candidate = Path.Combine(dir, stem + ext);
        int i = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            i++;
        }
        return candidate;
    }

    /// <summary>A new folder next to the input that doesn't clash with anything: "report pages", "report pages (1)"...</summary>
    public static string NewFolderNextTo(string input, string suffix)
    {
        var dir = OutputFolderFor(input);
        var stem = Path.GetFileNameWithoutExtension(input) + suffix;
        var candidate = Path.Combine(dir, stem);
        int i = 1;
        while (File.Exists(candidate) || Directory.Exists(candidate))
            candidate = Path.Combine(dir, $"{stem} ({i++})");
        Directory.CreateDirectory(candidate);
        return candidate;
    }

    /// <summary>A fresh scratch folder; caller deletes it with <see cref="TryDelete"/>.</summary>
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Droppo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* temp files, not worth crashing over */ }
    }

    public static string HumanSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return (bytes / (1024.0 * 1024.0)).ToString("0.0#", CultureInfo.InvariantCulture) + " MB";
    }

    public static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Sorts "scan 2" before "scan 10", like Explorer does.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        private static readonly Regex Chunks = new(@"\d+|\D+", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            var a = Chunks.Matches(x);
            var b = Chunks.Matches(y);
            for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            {
                var sa = a[i].Value;
                var sb = b[i].Value;
                int c;
                if (char.IsDigit(sa[0]) && char.IsDigit(sb[0]))
                {
                    var ta = sa.TrimStart('0');
                    var tb = sb.TrimStart('0');
                    c = ta.Length != tb.Length ? ta.Length.CompareTo(tb.Length) : string.CompareOrdinal(ta, tb);
                }
                else
                {
                    c = string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
                }
                if (c != 0) return c;
            }
            return a.Count.CompareTo(b.Count);
        }
    }
}
