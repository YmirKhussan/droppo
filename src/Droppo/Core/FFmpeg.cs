using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

public sealed class ToolMissingException : Exception
{
    public ToolMissingException(string message) : base(message) { }

    public static ToolMissingException FFmpeg() =>
        new("FFmpeg (for pictures, video and audio) wasn't found. Reinstall Droppo, or choose it in Settings > Helper programs.");

    public static ToolMissingException Qpdf() =>
        new("qpdf (for the PDF tools) wasn't found. Reinstall Droppo, or choose it in Settings > Helper programs.");

    public static ToolMissingException PdfRenderer() =>
        new("PDF page rendering isn't available on this system.");
}

/// <summary>Finds and runs ffmpeg. All the heavy media work is ffmpeg; we just build the arguments.</summary>
public sealed class FFmpeg
{
    public string ExePath { get; }

    public FFmpeg(string exePath) => ExePath = exePath;

    public static FFmpeg? TryCreate(string? configuredPath)
    {
        var path = Locate(configuredPath);
        return path == null ? null : new FFmpeg(path);
    }

    public static string? Locate(string? configuredPath)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var extra = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            extra.AddRange(ToolLocator.SearchUnder(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "*FFmpeg*", exe));
            extra.Add(@"C:\ffmpeg\bin\" + exe);
            extra.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", exe));
        }
        return ToolLocator.Locate("ffmpeg", configuredPath, extra);
    }

    /// <summary>Runs ffmpeg with the given arguments (no shell quoting needed). Throws with ffmpeg's own error on failure.</summary>
    public Task RunAsync(IEnumerable<string> args, CancellationToken ct = default)
    {
        var all = new[] { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" }.Concat(args);
        return ProcessRunner.RunAsync(ExePath, all, ct, friendly: Friendly);
    }

    /// <summary>Length, picture size and tracks of a media file (ffmpeg prints these when given no output).</summary>
    public async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct = default)
    {
        var (_, _, err) = await ProcessRunner.CaptureAsync(ExePath, new[] { "-hide_banner", "-nostdin", "-i", input }, ct).ConfigureAwait(false);
        return CompressOps.ParseProbe(err);
    }

    /// <summary>Turns the common ffmpeg errors into something a normal person can act on.</summary>
    private static string? Friendly(string err)
    {
        if (err.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
            return "This file has no track of that type (e.g. a video with no sound).";
        if (err.Contains("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase))
            return "FFmpeg can't read this file. It may be damaged, or your FFmpeg build doesn't support it.";
        if (err.Contains("Unknown encoder", StringComparison.OrdinalIgnoreCase) || err.Contains("Encoder not found", StringComparison.OrdinalIgnoreCase))
            return "This copy of FFmpeg is missing a part Droppo needs. Reinstalling Droppo puts the full one back.";
        if (err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Can't write to this folder (permission denied).";
        return null;
    }
}
