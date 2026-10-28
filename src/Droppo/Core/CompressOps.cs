using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>How small the person wants it: a size in MB, or a percentage of the original.</summary>
public sealed record SizeTarget(double Value, bool IsPercent)
{
    public long BytesFor(long originalBytes) =>
        IsPercent ? (long)(originalBytes * Value / 100.0) : (long)(Value * 1024 * 1024);

    public string Describe => IsPercent ? $"{Paths.Num(Value)}% of the original" : $"{Paths.Num(Value)} MB";

    /// <summary>Goes into the file name: photo-50%.jpg, video-8mb.mp4.</summary>
    public string Suffix => IsPercent ? $"-{Paths.Num(Value)}%" : $"-{Paths.Num(Value)}mb";
}

/// <summary>What ffmpeg can tell us about a media file without converting it.</summary>
public sealed record MediaInfo(double DurationSeconds, int Width, int Height, bool HasVideo, bool HasAudio);

/// <summary>"Smaller": one slice for every file type, aiming at a size the person picks.</summary>
public static class CompressOps
{
    public static WheelAction Action(string id, Func<string, ActionContext, Task<ActionOutput>> perFile) => new()
    {
        Id = id, Label = "Smaller", Hint = "pick a size", Prepare = AskTarget, PerFile = perFile,
    };

    /// <summary>Asks once for the whole batch and remembers the answer for next time.</summary>
    public static async Task<object?> AskTarget(IReadOnlyList<string> files, ActionContext ctx)
    {
        long largest = files.Where(File.Exists).Select(f => new FileInfo(f).Length).DefaultIfEmpty(0).Max();
        var last = new SizeTarget(ctx.Settings.CompressValue, ctx.Settings.CompressIsPercent);
        var picked = await ctx.Ask.AskTargetSizeAsync(largest, files.Count, last);
        if (picked == null) return null;
        ctx.Settings.CompressValue = picked.Value;
        ctx.Settings.CompressIsPercent = picked.IsPercent;
        ctx.Settings.Save();
        return picked;
    }

    public static SizeTarget Target(ActionContext ctx) =>
        ctx.State as SizeTarget ?? new SizeTarget(ctx.Settings.CompressValue, ctx.Settings.CompressIsPercent);

    // ---------- video ----------

    /// <summary>
    /// Two-pass H.264 aimed at the target size: work out the bitrate the size allows, lower the
    /// resolution if that bitrate is too thin for the original, then check and correct once.
    /// </summary>
    public static async Task<ActionOutput> Video(string input, ActionContext ctx)
    {
        var target = Target(ctx);
        long original = new FileInfo(input).Length;
        long goal = target.BytesFor(original);
        var name = Path.GetFileName(input);
        if (original <= goal) return new(null, $"{name} is already {Paths.HumanSize(original)}, nothing to do");

        var info = await ctx.Ff.ProbeAsync(input, ctx.Token);
        if (info.DurationSeconds <= 0) throw new InvalidOperationException("Can't tell how long this video is.");

        double totalKbps = goal * 8 / 1000.0 / info.DurationSeconds * 0.96; // leave room for the container
        int audioKbps = info.HasAudio ? (int)Math.Clamp(totalKbps * 0.12, 32, 128) : 0;
        double videoKbps = totalKbps - audioKbps;
        if (videoKbps < 60)
        {
            double minMb = (60 + (info.HasAudio ? 32 : 0)) * info.DurationSeconds / 8 / 1000 / 0.96 / 1.024 / 1.024;
            throw new InvalidOperationException($"Too small for a {Math.Round(info.DurationSeconds)}s video. Try at least {Math.Ceiling(minMb)} MB.");
        }

        // A thin bitrate at full resolution looks blocky; a smaller picture looks much better
        int? shortSide = videoKbps switch { >= 2500 => null, >= 1200 => 720, >= 600 => 480, >= 300 => 360, _ => 240 };
        if (shortSide.HasValue && Math.Min(info.Width, info.Height) is int s && s > 0 && s <= shortSide.Value) shortSide = null;
        string scale = shortSide is int side
            ? $"scale=w='if(gt(iw,ih),-2,min({side},iw))':h='if(gt(iw,ih),min({side},ih),-2)'"
            : "scale=trunc(iw/2)*2:trunc(ih/2)*2";

        var output = Paths.NextTo(input, target.Suffix, ".mp4");
        var tmp = Paths.NewTempDir();
        try
        {
            var log = Path.Combine(tmp, "pass");
            await ctx.Ff.RunAsync(new[]
            {
                "-i", input, "-vf", scale, "-c:v", "libx264", "-preset", "veryfast", "-b:v", $"{(int)videoKbps}k",
                "-pass", "1", "-passlogfile", log, "-an", "-f", "null", "-",
            }, ctx.Token);

            async Task Encode(double kbps)
            {
                var args = new List<string>
                {
                    "-i", input, "-vf", scale, "-c:v", "libx264", "-preset", "veryfast", "-b:v", $"{(int)kbps}k",
                    "-pass", "2", "-passlogfile", log, "-pix_fmt", "yuv420p", "-movflags", "+faststart",
                };
                if (info.HasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", $"{audioKbps}k" });
                else args.Add("-an");
                args.Add(output);
                await ctx.Ff.RunAsync(args, ctx.Token);
            }

            double kbps = videoKbps;
            await Encode(kbps);
            long size = new FileInfo(output).Length;
            for (int attempt = 0; attempt < 3 && size > goal; attempt++)
            {
                kbps *= (double)goal / size * 0.93; // overshot: aim a bit lower and redo the second pass
                if (kbps < 30) break;
                await Encode(kbps);
                size = new FileInfo(output).Length;
            }
            if (size > goal * 1.05)
                throw new InvalidOperationException($"Couldn't get it down to {target.Describe}. Try a slightly bigger target.");

            var resized = shortSide.HasValue ? $", resized to {shortSide}p" : "";
            return new(output, $"{name}: {Paths.HumanSize(original)} → {Paths.HumanSize(size)}{resized}");
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // ---------- audio ----------

    public static async Task<ActionOutput> Audio(string input, ActionContext ctx)
    {
        var target = Target(ctx);
        long original = new FileInfo(input).Length;
        long goal = target.BytesFor(original);
        var name = Path.GetFileName(input);
        if (original <= goal) return new(null, $"{name} is already {Paths.HumanSize(original)}, nothing to do");

        var info = await ctx.Ff.ProbeAsync(input, ctx.Token);
        if (info.DurationSeconds <= 0) throw new InvalidOperationException("Can't tell how long this recording is.");

        double kbps = goal * 8 / 1000.0 / info.DurationSeconds * 0.96;
        int[] steps = { 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
        if (kbps < steps[0]) throw new InvalidOperationException("That's too small for this recording, even at the lowest quality.");
        int index = Array.FindLastIndex(steps, r => r <= kbps);

        var output = Paths.NextTo(input, target.Suffix, ".mp3");
        long size = 0;
        for (; index >= 0; index--)
        {
            int rate = steps[index];
            var args = new List<string> { "-i", input, "-vn", "-c:a", "libmp3lame", "-b:a", $"{rate}k" };
            if (rate <= 48) args.AddRange(new[] { "-ac", "1" });             // mono sounds far better than squeezed stereo
            if (rate < 32) args.AddRange(new[] { "-ar", rate <= 16 ? "16000" : "22050" }); // MP3 only goes this low at lower sample rates
            args.Add(output);
            await ctx.Ff.RunAsync(args, ctx.Token);
            size = new FileInfo(output).Length;
            if (size <= goal) return new(output, $"{name}: {Paths.HumanSize(original)} → {Paths.HumanSize(size)} ({rate} kbps)");
        }
        throw new InvalidOperationException($"Couldn't get it down to {target.Describe}. Try a slightly bigger target.");
    }

    // ---------- probing ----------

    private static readonly Regex DurationRx = new(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(@"\b(\d{2,5})x(\d{2,5})\b", RegexOptions.Compiled);
    private static readonly Regex RotationRx = new(@"rotation of (-?\d+(?:\.\d+)?) degrees", RegexOptions.Compiled);

    public static MediaInfo ParseProbe(string ffmpegStderr)
    {
        double duration = 0;
        var d = DurationRx.Match(ffmpegStderr);
        if (d.Success)
        {
            duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 +
                       double.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        int w = 0, h = 0;
        bool hasVideo = false, hasAudio = false;
        foreach (var line in ffmpegStderr.Split('\n'))
        {
            if (!line.Contains("Stream #")) continue;
            if (line.Contains("Video:") && !line.Contains("attached pic"))
            {
                if (!hasVideo)
                {
                    var m = SizeRx.Match(line[line.IndexOf("Video:", StringComparison.Ordinal)..]);
                    if (m.Success) { w = int.Parse(m.Groups[1].Value); h = int.Parse(m.Groups[2].Value); }
                }
                hasVideo = true;
            }
            if (line.Contains("Audio:")) hasAudio = true;
        }
        // phone videos store landscape frames plus a "rotate" flag; ffmpeg turns them upright, so report it upright
        var rot = RotationRx.Match(ffmpegStderr);
        if (rot.Success && Math.Abs(Math.Round(double.Parse(rot.Groups[1].Value, CultureInfo.InvariantCulture))) % 180 == 90)
            (w, h) = (h, w);
        return new MediaInfo(duration, w, h, hasVideo, hasAudio);
    }
}
