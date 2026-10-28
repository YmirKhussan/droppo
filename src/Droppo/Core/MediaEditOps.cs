using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>The part of a clip to keep, in seconds.</summary>
public sealed record TrimRange(double Start, double End)
{
    public double Length => Math.Max(0, End - Start);
}

/// <summary>Trim, rotate and join for video and audio.</summary>
public static class MediaEditOps
{
    private static string Sec(double s) => s.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------- trim ----------

    public static WheelAction TrimAction(string id) => new()
    {
        Id = id, Label = "Trim", Hint = "cut start/end", Prepare = AskTrim, PerFile = Trim,
    };

    private static async Task<object?> AskTrim(IReadOnlyList<string> files, ActionContext ctx)
    {
        string file = files.First(File.Exists);
        MediaInfo info = await ctx.Ff.ProbeAsync(file, ctx.Token);
        if (info.DurationSeconds <= 0) throw new InvalidOperationException("Can't tell how long this file is.");
        string previewDir = Paths.NewTempDir();
        try
        {
            async Task<string?> Frame(double t)
            {
                if (!info.HasVideo) return null;
                var png = Path.Combine(previewDir, $"f{Guid.NewGuid():N}.png");
                try
                {
                    await ctx.Ff.RunAsync(new[] { "-ss", Sec(t), "-i", file, "-frames:v", "1", "-vf", "scale=480:-2", "-update", "1", png }, ctx.Token);
                    return File.Exists(png) ? png : null;
                }
                catch (ToolException)
                {
                    return null;
                }
            }
            return await ctx.Ask.AskTrimAsync(file, info.DurationSeconds, info.HasVideo, Frame);
        }
        finally
        {
            Paths.TryDelete(previewDir);
        }
    }

    public static async Task<ActionOutput> Trim(string input, ActionContext ctx)
    {
        var range = ctx.State as TrimRange ?? throw new InvalidOperationException("No trim range chosen.");
        bool video = FileKinds.Of(input) == FileKind.Video;
        var ext = video ? ".mp4" : AudioExt(input);
        var output = Paths.NextTo(input, "-trim", ext);
        var args = new List<string> { "-ss", Sec(range.Start), "-i", input, "-t", Sec(range.Length) };
        if (video)
            args.AddRange(new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" });
        else
            args.AddRange(AudioCodec(ext));
        args.Add(output);
        await ctx.Ff.RunAsync(args, ctx.Token);
        return new(output, $"Kept {FormatTime(range.Start)} to {FormatTime(range.End)} ({FormatTime(range.Length)})");
    }

    public static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss\.f");
    }

    // ---------- rotate ----------

    public static Task<ActionOutput> RotateVideo(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "-rotated", ".mp4");
        return Run(ctx, output, "-i", input, "-vf", "transpose=1", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart");
    }

    // ---------- join ----------

    public static WheelAction JoinAction(string id, bool video) => new()
    {
        Id = id, Label = "Join", Hint = "set the order",
        Prepare = (files, ctx) => AskOrder(files, ctx, video ? "Join videos" : "Join recordings"),
        Combined = video ? JoinVideos : JoinAudio,
    };

    private static async Task<object?> AskOrder(IReadOnlyList<string> files, ActionContext ctx, string title)
    {
        var start = files.Where(File.Exists).OrderBy(f => Path.GetFileName(f), Paths.NaturalComparer.Instance).ToList();
        return await ctx.Ask.AskOrderAsync(title, "Use the arrows to set the order. Top plays first.", start);
    }

    private static IReadOnlyList<string> Ordered(IReadOnlyList<string> files, ActionContext ctx) =>
        ctx.State as IReadOnlyList<string> ?? files.Where(File.Exists).OrderBy(f => Path.GetFileName(f), Paths.NaturalComparer.Instance).ToList();

    /// <summary>
    /// Joins clips even when they differ: every clip is scaled to the first one's size (with bars if the
    /// shape differs), set to 30 fps, and clips without sound get silence so the soundtrack lines up.
    /// </summary>
    public static async Task<ActionOutput> JoinVideos(IReadOnlyList<string> files, ActionContext ctx)
    {
        var clips = Ordered(files, ctx);
        if (clips.Count < 2) throw new InvalidOperationException("Pick two or more videos to join.");

        var infos = new List<MediaInfo>();
        foreach (var c in clips) infos.Add(await ctx.Ff.ProbeAsync(c, ctx.Token));
        int w = Even(infos[0].Width > 0 ? infos[0].Width : 1280), h = Even(infos[0].Height > 0 ? infos[0].Height : 720);

        var args = new List<string>();
        foreach (var c in clips) args.AddRange(new[] { "-i", c });
        int extra = clips.Count;
        var silence = new Dictionary<int, int>();
        for (int i = 0; i < clips.Count; i++)
        {
            if (infos[i].HasAudio) continue;
            args.AddRange(new[] { "-f", "lavfi", "-t", Sec(Math.Max(0.1, infos[i].DurationSeconds)), "-i", "anullsrc=r=48000:cl=stereo" });
            silence[i] = extra++;
        }

        var graph = new System.Text.StringBuilder();
        for (int i = 0; i < clips.Count; i++)
        {
            graph.Append($"[{i}:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30,format=yuv420p[v{i}];");
            var audioIn = silence.TryGetValue(i, out var s) ? $"[{s}:a]" : $"[{i}:a]";
            graph.Append($"{audioIn}aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        }
        for (int i = 0; i < clips.Count; i++) graph.Append($"[v{i}][a{i}]");
        graph.Append($"concat=n={clips.Count}:v=1:a=1[v][a]");

        var output = Paths.NextTo(clips[0], "-joined", ".mp4");
        args.AddRange(new[]
        {
            "-filter_complex", graph.ToString(), "-map", "[v]", "-map", "[a]",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", output,
        });
        await ctx.Ff.RunAsync(args, ctx.Token);
        return new(output, $"{clips.Count} clips joined, {FormatTime(infos.Sum(i => i.DurationSeconds))} total");
    }

    public static async Task<ActionOutput> JoinAudio(IReadOnlyList<string> files, ActionContext ctx)
    {
        var parts = Ordered(files, ctx);
        if (parts.Count < 2) throw new InvalidOperationException("Pick two or more recordings to join.");
        var args = new List<string>();
        foreach (var p in parts) args.AddRange(new[] { "-i", p });
        var graph = new System.Text.StringBuilder();
        for (int i = 0; i < parts.Count; i++) graph.Append($"[{i}:a]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        for (int i = 0; i < parts.Count; i++) graph.Append($"[a{i}]");
        graph.Append($"concat=n={parts.Count}:v=0:a=1[a]");
        var output = Paths.NextTo(parts[0], "-joined", ".mp3");
        args.AddRange(new[] { "-filter_complex", graph.ToString(), "-map", "[a]", "-c:a", "libmp3lame", "-q:a", "2", output });
        await ctx.Ff.RunAsync(args, ctx.Token);
        return new(output, $"{parts.Count} recordings joined");
    }

    // ---------- helpers ----------

    private static int Even(int v) => Math.Max(2, v / 2 * 2);

    private static string AudioExt(string input)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        return ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".opus" ? ext : ".mp3";
    }

    private static string[] AudioCodec(string ext) => ext switch
    {
        ".wav" => new[] { "-c:a", "pcm_s16le" },
        ".flac" => new[] { "-c:a", "flac" },
        ".m4a" => new[] { "-c:a", "aac", "-b:a", "192k" },
        ".ogg" or ".opus" => new[] { "-c:a", "libopus", "-b:a", "96k" },
        _ => new[] { "-c:a", "libmp3lame", "-q:a", "2" },
    };

    private static async Task<ActionOutput> Run(ActionContext ctx, string output, params string[] args)
    {
        await ctx.Ff.RunAsync(args.Append(output), ctx.Token);
        return new(output);
    }
}
