using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>Video and audio actions. All ffmpeg.</summary>
public static class AvOps
{
    private const string EvenSize = "scale=trunc(iw/2)*2:trunc(ih/2)*2";

    public static List<WheelAction> VideoActions(Settings s, int count)
    {
        var list = new List<WheelAction>
        {
            new() { Id = "vid.mp4", Label = "MP4", Hint = "plays anywhere", PerFile = ToMp4, HideForExtensions = new[] { ".mp4" } },
            new() { Id = "vid.gif", Label = "GIF", Hint = $"first {s.GifMaxSeconds}s", PerFile = ToGif },
            new() { Id = "vid.mp3", Label = "MP3", Hint = "audio only", PerFile = ExtractMp3 },
            CompressOps.Action("vid.smaller", CompressOps.Video),
        };
        if (count == 1) list.Add(MediaEditOps.TrimAction("vid.trim"));
        list.Add(new() { Id = "vid.rotate", Label = "Rotate", Hint = "90\u00B0 clockwise", PerFile = MediaEditOps.RotateVideo });
        list.Add(new() { Id = "vid.mute", Label = "Mute", Hint = "remove audio", PerFile = Mute });
        list.Add(new() { Id = "vid.frame", Label = "Frame", Hint = "snapshot JPG", PerFile = Snapshot });
        if (count > 1)
        {
            list.Add(MediaEditOps.JoinAction("vid.join", video: true));
            list.Add(RenameOps.Action());
        }
        return list;
    }

    public static List<WheelAction> AudioActions(int count)
    {
        var list = new List<WheelAction>
        {
            new() { Id = "aud.mp3", Label = "MP3", Hint = "convert", PerFile = ExtractMp3, HideForExtensions = new[] { ".mp3" } },
            new() { Id = "aud.wav", Label = "WAV", Hint = "convert", PerFile = ToWav, HideForExtensions = new[] { ".wav" } },
            new() { Id = "aud.m4a", Label = "M4A", Hint = "convert", PerFile = ToM4a, HideForExtensions = new[] { ".m4a" } },
            new() { Id = "aud.flac", Label = "FLAC", Hint = "lossless", PerFile = ToFlac, HideForExtensions = new[] { ".flac" } },
            new() { Id = "aud.loud", Label = "Louder", Hint = "even volume", PerFile = Normalize },
            CompressOps.Action("aud.smaller", CompressOps.Audio),
        };
        if (count == 1) list.Add(MediaEditOps.TrimAction("aud.trim"));
        else
        {
            list.Add(MediaEditOps.JoinAction("aud.join", video: false));
            list.Add(RenameOps.Action());
        }
        return list;
    }

    private static async Task<ActionOutput> Run(ActionContext ctx, string output, params string[] args)
    {
        await ctx.Ff.RunAsync(args.Append(output), ctx.Token);
        return new(output);
    }

    private static readonly string[] H264 =
    {
        "-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
    };

    public static Task<ActionOutput> ToMp4(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".mp4"),
            new[] { "-i", input, "-vf", EvenSize }.Concat(H264).Concat(new[] { "-crf", "20", "-c:a", "aac", "-b:a", "160k" }).ToArray());

    public static Task<ActionOutput> ToGif(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".gif"),
            "-t", ctx.Settings.GifMaxSeconds.ToString(), "-i", input,
            "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4",
            "-loop", "0");

    public static Task<ActionOutput> ExtractMp3(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".mp3"), "-i", input, "-vn", "-c:a", "libmp3lame", "-q:a", "2");

    public static Task<ActionOutput> Mute(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "-muted", Path.GetExtension(input)), "-i", input, "-c", "copy", "-an");

    public static async Task<ActionOutput> Snapshot(string input, ActionContext ctx)
    {
        var output = Paths.NextTo(input, "-frame", ".jpg");
        // 1 second in skips black first frames; very short clips fall back to the first frame
        await ctx.Ff.RunAsync(new[] { "-ss", "1", "-i", input, "-frames:v", "1", "-q:v", "2", "-update", "1", output }, ctx.Token);
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            await ctx.Ff.RunAsync(new[] { "-i", input, "-frames:v", "1", "-q:v", "2", "-update", "1", output }, ctx.Token);
        return new(output);
    }

    public static Task<ActionOutput> ToWav(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".wav"), "-i", input, "-vn", "-c:a", "pcm_s16le");

    public static Task<ActionOutput> ToM4a(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".m4a"), "-i", input, "-vn", "-c:a", "aac", "-b:a", "192k");

    public static Task<ActionOutput> ToFlac(string input, ActionContext ctx) =>
        Run(ctx, Paths.NextTo(input, "", ".flac"), "-i", input, "-vn", "-c:a", "flac");

    /// <summary>Evens out quiet/loud recordings (voice notes, lectures) to a standard loudness.</summary>
    public static Task<ActionOutput> Normalize(string input, ActionContext ctx)
    {
        var ext = Path.GetExtension(input).ToLowerInvariant();
        (string outExt, string[] codec) = ext switch
        {
            ".wav" => (".wav", new[] { "-c:a", "pcm_s16le" }),
            ".flac" => (".flac", new[] { "-c:a", "flac" }),
            ".m4a" or ".aac" => (ext, new[] { "-c:a", "aac", "-b:a", "192k" }),
            ".ogg" or ".opus" => (ext, new[] { "-c:a", "libopus", "-b:a", "96k" }),
            _ => (".mp3", new[] { "-c:a", "libmp3lame", "-q:a", "2" }),
        };
        var args = new[] { "-i", input, "-vn", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000" }.Concat(codec).ToArray();
        return Run(ctx, Paths.NextTo(input, "-loud", outExt), args);
    }
}
