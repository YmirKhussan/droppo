using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Droppo.Core;

public enum FileKind
{
    None,
    Image,
    Video,
    Audio,
    Pdf,
    Office,
    Other,
    Mixed,
}

public static class FileKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff",
        ".heic", ".heif", ".avif", ".ico",
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".wmv", ".flv", ".3gp", ".mts", ".ts",
    };

    private static readonly HashSet<string> Audios = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".amr",
    };

    private static readonly HashSet<string> OfficeDocs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".rtf", ".odt", ".ppt", ".pptx", ".odp", ".xls", ".xlsx", ".ods",
    };

    public static FileKind Of(string path)
    {
        if (Directory.Exists(path)) return FileKind.Other;
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audios.Contains(ext)) return FileKind.Audio;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FileKind.Pdf;
        if (OfficeDocs.Contains(ext)) return FileKind.Office;
        return FileKind.Other;
    }

    public static FileKind OfAll(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return FileKind.None;
        var kinds = paths.Select(Of).Distinct().ToList();
        return kinds.Count == 1 ? kinds[0] : FileKind.Mixed;
    }

    /// <summary>"2 images", "1 video", "3 files" etc, for the centre of the wheel.</summary>
    public static string Describe(IReadOnlyList<string> paths)
    {
        var kind = OfAll(paths);
        var n = paths.Count;
        string noun = kind switch
        {
            FileKind.Image => n == 1 ? "image" : "images",
            FileKind.Video => n == 1 ? "video" : "videos",
            FileKind.Audio => n == 1 ? "audio file" : "audio files",
            FileKind.Pdf => n == 1 ? "PDF" : "PDFs",
            FileKind.Office => n == 1 ? "document" : "documents",
            _ => n == 1 ? (Directory.Exists(paths[0]) ? "folder" : "file") : "items",
        };
        return $"{n} {noun}";
    }
}
