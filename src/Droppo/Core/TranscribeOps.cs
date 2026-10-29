using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>A Whisper speech model Droppo can download once and keep.</summary>
public sealed record SpeechModel(string Id, string Label, string Detail, string FileName, long ApproxBytes);

/// <summary>One line of speech with its time on the clip.</summary>
public sealed record SpeechSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// Speech to text, fully offline: ffmpeg pulls the sound out, whisper.cpp (MIT, a C++ port of
/// OpenAI's Whisper) listens to it. The model is downloaded once on first use, then nothing leaves the PC.
/// </summary>
public static class TranscribeOps
{
    public static readonly SpeechModel[] Models =
    {
        new("base", "Quick", "148 MB download. Good for clear speech, about 3x faster.", "ggml-base.bin", 147_951_465),
        new("small", "Accurate", "488 MB download. Better with accents, background noise and languages other than English.", "ggml-small.bin", 487_601_967),
    };

    /// <summary>Where whisper.cpp's own download script gets the models from.</summary>
    public const string ModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    public static string ModelFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Droppo", "models");

    public static SpeechModel ModelFor(string? id) => Models.FirstOrDefault(m => m.Id == id) ?? Models[0];

    public static string? LocateWhisper(string? configured) => ToolLocator.Locate("whisper-cli", configured);

    public static WheelAction Action(string id, bool video) => new()
    {
        Id = id,
        Label = "Transcribe",
        Hint = "speech to text",
        Prepare = PrepareAsync,
        PerFile = (file, ctx) => TranscribeAsync(file, ctx, subtitles: video),
    };

    private sealed record Plan(string Whisper, SpeechModel Model);

    /// <summary>Checks the engine is there and a model is picked (asks once, the first time).</summary>
    private static async Task<object?> PrepareAsync(IReadOnlyList<string> files, ActionContext ctx)
    {
        var whisper = LocateWhisper(ctx.Settings.WhisperPath)
            ?? throw new ToolMissingException("The speech-to-text engine (whisper) wasn't found. Reinstalling Droppo puts it back.");
        _ = ctx.Ff; // the sound has to come out of the file first

        var chosen = ctx.Settings.TranscribeModel;
        var model = ModelFor(chosen);
        if (FindModel(model) != null && !string.IsNullOrEmpty(chosen)) return new Plan(whisper, model);

        // nothing picked yet but one is already on disk (copied over, or an earlier install): just use it
        if (string.IsNullOrEmpty(chosen) && Models.FirstOrDefault(m => FindModel(m) != null) is { } have)
        {
            ctx.Settings.TranscribeModel = have.Id;
            ctx.Settings.Save();
            return new Plan(whisper, have);
        }

        var picked = await ctx.Ask.AskChoiceAsync(new ChoiceRequest(
            "Transcribe",
            "Transcribe needs a speech model",
            "It's a one-time download. After that everything runs on this PC, and nothing you transcribe is uploaded.",
            Models.Select(m => new Choice(m.Id, m.Label, m.Detail)).ToList(),
            model.Id,
            "Download")).ConfigureAwait(false);
        if (picked == null) return null;

        model = ModelFor(picked);
        ctx.Settings.TranscribeModel = model.Id;
        ctx.Settings.Save();
        return new Plan(whisper, model);
    }

    private static async Task<ActionOutput> TranscribeAsync(string input, ActionContext ctx, bool subtitles)
    {
        var plan = ctx.State as Plan
            ?? await PrepareAsync(new[] { input }, ctx).ConfigureAwait(false) as Plan
            ?? throw new OperationCanceledException();
        var modelPath = await EnsureModelAsync(plan.Model, ctx.ProgressChanged, ctx.Token).ConfigureAwait(false);

        var tmp = Paths.NewTempDir();
        try
        {
            ctx.Report(0, "Getting the sound ready…");
            var info = await ctx.Ff.ProbeAsync(input, ctx.Token).ConfigureAwait(false);
            if (info.DurationSeconds > 0 && !info.HasAudio && info.HasVideo)
                throw new ToolException("This video has no sound, so there's nothing to transcribe.");

            // Whisper wants 16 kHz mono
            var wav = Path.Combine(tmp, "audio.wav");
            await ctx.Ff.RunAsync(new[] { "-i", input, "-vn", "-sn", "-dn", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav }, ctx.Token).ConfigureAwait(false);

            var result = await RunWhisperAsync(plan.Whisper, modelPath, wav, Path.Combine(tmp, "out"),
                p => ctx.Report(0.03 + 0.97 * p, $"Transcribing… {Math.Round(p * 100)}%"), ctx.Token).ConfigureAwait(false);

            var segments = Clean(result.Segments);
            if (segments.Count == 0) return new ActionOutput(null, "We couldn't hear any speech in this file.");

            var txt = Paths.NextTo(input, "-transcript", ".txt");
            File.WriteAllText(txt, ToTranscript(segments), new UTF8Encoding(true));

            var notes = new List<string>();
            var language = LanguageName(result.Language);
            double seconds = info.DurationSeconds > 0 ? info.DurationSeconds : segments[^1].End.TotalSeconds;
            notes.Add(language != null ? $"{language} · {HumanDuration(seconds)}" : HumanDuration(seconds));
            if (subtitles)
            {
                var srt = Paths.NextTo(input, "", ".srt");
                File.WriteAllText(srt, ToSrt(segments), new UTF8Encoding(false));
                notes.Add($"Subtitles too: {Path.GetFileName(srt)}");
            }
            return new ActionOutput(txt, string.Join("\n", notes));
        }
        finally
        {
            Paths.TryDelete(tmp);
        }
    }

    // ------------------------------------------------------------------ whisper.cpp

    public sealed record WhisperResult(IReadOnlyList<SpeechSegment> Segments, string? Language);

    private static readonly Regex ProgressRx = new(@"progress\s*=\s*(\d+)\s*%", RegexOptions.Compiled);
    private static readonly Regex LanguageRx = new(@"auto-detected language:\s*([a-z]{2,3})\b", RegexOptions.Compiled);

    /// <summary>Runs whisper-cli on a 16 kHz WAV and reads the subtitles it writes. progress gets 0 to 1.</summary>
    public static async Task<WhisperResult> RunWhisperAsync(string whisper, string model, string wav, string outPrefix,
        Action<double>? progress, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-m", AsciiPath(model),
            "-f", AsciiPath(wav),
            "-l", "auto",
            "-t", Threads().ToString(CultureInfo.InvariantCulture),
            "-osrt",
            "-of", AsciiPath(outPrefix),
            "-pp",
        };

        string? language = null;
        var (exit, _, err) = await ProcessRunner.StreamAsync(whisper, args, ct, line =>
        {
            var m = ProgressRx.Match(line);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var pct)) progress?.Invoke(pct / 100.0);
            var l = LanguageRx.Match(line);
            if (l.Success) language = l.Groups[1].Value;
        }).ConfigureAwait(false);

        if (exit != 0)
        {
            Log.Write($"whisper-cli failed ({exit}): {string.Join(' ', args)}{Environment.NewLine}  {Tail(err, 12)}");
            throw new ToolException(FriendlyWhisperError(exit, err, model));
        }

        var srtPath = outPrefix + ".srt";
        if (!File.Exists(srtPath))
        {
            Log.Write("whisper-cli finished but wrote no subtitles:" + Environment.NewLine + Tail(err, 12));
            throw new ToolException("The speech engine finished but didn't save anything. The log has the details.");
        }
        progress?.Invoke(1);
        return new WhisperResult(ParseSrt(File.ReadAllText(srtPath, Encoding.UTF8)), language);
    }

    private static int Threads()
    {
        int n = Environment.ProcessorCount;
        return Math.Min(8, Math.Max(Math.Min(4, n), n - 2)); // leave a little room so the PC stays usable
    }

    private static string FriendlyWhisperError(int exit, string err, string model)
    {
        // Windows' own crash codes: a DLL missing, or a processor without the instructions it was built for
        if (exit == unchecked((int)0xC0000135) || exit == unchecked((int)0xC0000139))
            return "The speech engine is missing a file. Reinstalling Droppo puts it back.";
        if (exit == unchecked((int)0xC000001D))
            return "This PC's processor is too old for the speech engine (it needs AVX support).";
        if (err.Contains("failed to load model", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("failed to initialize whisper context", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("invalid model", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(model); } catch { }
            return "The speech model was damaged, so it's been removed. Try again and it will download a fresh copy.";
        }
        if (err.Contains("failed to read", StringComparison.OrdinalIgnoreCase) || err.Contains("failed to open", StringComparison.OrdinalIgnoreCase))
            return "The speech engine couldn't read the sound from this file.";
        var tail = Tail(err, 2).Replace(Environment.NewLine, " | ");
        return string.IsNullOrWhiteSpace(tail) ? $"The speech engine stopped (code {exit})." : tail;
    }

    private static string Tail(string text, int lines) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(lines));

    // ------------------------------------------------------------------ the model

    /// <summary>The model file if it's already on this PC (downloaded before, or shipped next to the engine).</summary>
    public static string? FindModel(SpeechModel model)
    {
        var candidates = new[]
        {
            Path.Combine(ModelFolder, model.FileName),
            Path.Combine(AppContext.BaseDirectory, "tools", "whisper-cli", "models", model.FileName),
        };
        foreach (var c in candidates)
        {
            try
            {
                if (File.Exists(c) && new FileInfo(c).Length > 10_000_000) return c;
            }
            catch
            {
                // unreadable, skip
            }
        }
        return null;
    }

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    /// <summary>Downloads the model the first time (with progress), then returns its path.</summary>
    public static async Task<string> EnsureModelAsync(SpeechModel model, Action<double, string?>? progress, CancellationToken ct,
        string? baseUrl = null)
    {
        var found = FindModel(model);
        if (found != null) return found;

        await DownloadLock.WaitAsync(ct).ConfigureAwait(false);
        string target = Path.Combine(ModelFolder, model.FileName);
        string part = target + ".part";
        try
        {
            found = FindModel(model);
            if (found != null) return found;
            Directory.CreateDirectory(ModelFolder);

            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Droppo/1.0");
            var url = (baseUrl ?? ModelBaseUrl) + model.FileName;
            Log.Write("Downloading speech model " + url);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ToolException($"Couldn't download the speech model (the server said {(int)response.StatusCode}). Try again later.");

            long? length = response.Content.Headers.ContentLength;
            long total = length ?? model.ApproxBytes;
            long got = 0;
            var clock = Stopwatch.StartNew();
            long lastReport = -1000;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    got += n;
                    if (clock.ElapsedMilliseconds - lastReport >= 250)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress?.Invoke((double)got / Math.Max(1, total),
                            $"Getting the speech model (one time): {got / 1_000_000} of {total / 1_000_000} MB");
                    }
                }
            }
            if (length is long expected && got != expected)
                throw new ToolException("The speech model download was cut off. Try again.");
            if (!LooksLikeModel(part))
                throw new ToolException("The speech model download looks damaged. Try again.");

            File.Move(part, target, overwrite: true);
            Log.Write($"Speech model saved: {target} ({Paths.HumanSize(got)})");
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new ToolException("Couldn't download the speech model. Check the internet connection and try again. (" + ex.Message + ")");
        }
        catch (IOException ex) when (ex is not FileNotFoundException)
        {
            throw new ToolException("Couldn't download the speech model: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            DownloadLock.Release();
        }
    }

    /// <summary>whisper.cpp model files start with the "ggml" magic number.</summary>
    public static bool LooksLikeModel(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            if (f.Length < 10_000_000) return false;
            var head = new byte[4];
            if (f.Read(head, 0, 4) != 4) return false;
            var s = Encoding.ASCII.GetString(head);
            return s is "lmgg" or "ggml";
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ subtitles and text

    private static readonly Regex TimeRx = new(@"(\d+):(\d{2}):(\d{2})[,.](\d{1,3})\s*-->\s*(\d+):(\d{2}):(\d{2})[,.](\d{1,3})", RegexOptions.Compiled);

    public static List<SpeechSegment> ParseSrt(string srt)
    {
        var list = new List<SpeechSegment>();
        var blocks = Regex.Split(srt.Replace("\r\n", "\n").Replace('\r', '\n').Trim('﻿', '\n', ' '), @"\n\s*\n");
        foreach (var block in blocks)
        {
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            int timeLine = lines.FindIndex(l => TimeRx.IsMatch(l));
            if (timeLine < 0) continue;
            var m = TimeRx.Match(lines[timeLine]);
            var text = string.Join(" ", lines.Skip(timeLine + 1));
            list.Add(new SpeechSegment(Time(m, 1), Time(m, 5), text));
        }
        return list;
    }

    private static TimeSpan Time(Match m, int g)
    {
        int ms = int.Parse(m.Groups[g + 3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
        return new TimeSpan(0, int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[g + 1].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups[g + 2].Value, CultureInfo.InvariantCulture), ms);
    }

    /// <summary>Drops Whisper's markers for silence and any empty lines; tidies spaces.</summary>
    public static List<SpeechSegment> Clean(IEnumerable<SpeechSegment> segments)
    {
        var list = new List<SpeechSegment>();
        foreach (var s in segments)
        {
            var text = Regex.Replace(s.Text, @"\[(BLANK_AUDIO|blank_audio|BLANK AUDIO)\]", "");
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            // the same line over and over is Whisper hearing things in silence
            if (list.Count >= 2 && list[^1].Text == text && list[^2].Text == text) continue;
            list.Add(s with { Text = text });
        }
        return list;
    }

    /// <summary>Readable text: lines run together, with a new paragraph wherever the speaker pauses.</summary>
    public static string ToTranscript(IReadOnlyList<SpeechSegment> segments)
    {
        var sb = new StringBuilder();
        int paragraph = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (i > 0)
            {
                var gap = s.Start - segments[i - 1].End;
                bool sentenceEnd = Regex.IsMatch(segments[i - 1].Text, @"[.!?。！？]['""”)]*$");
                if (gap.TotalSeconds >= 2.0 || (sentenceEnd && paragraph > 700))
                {
                    sb.Append(Environment.NewLine).Append(Environment.NewLine);
                    paragraph = 0;
                }
                else
                {
                    sb.Append(' ');
                }
            }
            sb.Append(s.Text);
            paragraph += s.Text.Length;
        }
        sb.Append(Environment.NewLine);
        return sb.ToString();
    }

    public static string ToSrt(IReadOnlyList<SpeechSegment> segments)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            sb.Append(i + 1).Append("\r\n")
              .Append(SrtTime(s.Start)).Append(" --> ").Append(SrtTime(s.End)).Append("\r\n")
              .Append(s.Text).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    private static string SrtTime(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";

    public static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        try
        {
            var name = CultureInfo.GetCultureInfo(code).EnglishName;
            return string.IsNullOrWhiteSpace(name) || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    public static string HumanDuration(double seconds)
    {
        if (seconds < 1) return "under a second";
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes} min";
        if (t.TotalMinutes >= 1) return t.Seconds == 0 ? $"{t.Minutes} min" : $"{t.Minutes} min {t.Seconds} s";
        return $"{t.Seconds} s";
    }

    // ------------------------------------------------------------------ Windows paths

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    /// <summary>
    /// whisper-cli reads its arguments in the old ANSI code page, so a path with letters outside it
    /// (e.g. a Chinese user name) won't open. The short 8.3 form of the same path is plain ASCII.
    /// </summary>
    public static string AsciiPath(string path)
    {
        if (path.All(c => c < 128) || !OperatingSystem.IsWindows()) return path;
        try
        {
            // the file may not exist yet (output prefix): shorten the folder, keep the (ASCII) name
            var dir = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);
            string target = File.Exists(path) || Directory.Exists(path) ? path : dir ?? path;
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(target, sb, (uint)sb.Capacity);
            if (n == 0 || n >= sb.Capacity) return path;
            var shortPath = sb.ToString();
            if (target != path) shortPath = Path.Combine(shortPath, name);
            return shortPath.All(c => c < 128) ? shortPath : path;
        }
        catch
        {
            return path;
        }
    }
}
