using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Droppo.Core;

namespace Droppo.Shell;

/// <summary>
/// Checks used by the GitHub build on a real Windows machine: runs the real engines on real files
/// and writes what happened to results.txt, so a change can be verified without a PC at hand.
/// </summary>
internal static class SelfTest
{
    public static async Task TranscribeAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            File.WriteAllText(Path.Combine(folder, "results.txt"), report.ToString());
        }

        var settings = new Settings { TranscribeModel = "base", OutputMode = "SameFolder" };
        var ff = FFmpeg.TryCreate(null);
        var whisper = TranscribeOps.LocateWhisper(null);
        Say($"ffmpeg:  {ff?.ExePath ?? "NOT FOUND"}");
        Say($"whisper: {whisper ?? "NOT FOUND"}");
        Say($"threads: {Environment.ProcessorCount} logical");

        var model = TranscribeOps.ModelFor("base");
        var clock = Stopwatch.StartNew();
        int lastTenth = -1;
        string modelPath;
        try
        {
            modelPath = await TranscribeOps.EnsureModelAsync(model, (f, text) =>
            {
                int tenth = (int)(f * 10);
                if (tenth != lastTenth) { lastTenth = tenth; Say($"  download {f:P0}: {text}"); }
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Say("MODEL DOWNLOAD FAILED: " + ex);
            return;
        }
        var size = new FileInfo(modelPath).Length;
        string sha1;
        using (var stream = File.OpenRead(modelPath)) sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
        Say($"model:   {modelPath}");
        Say($"         {size} bytes (expected about {model.ApproxBytes}), sha1 {sha1}, looks like a model: {TranscribeOps.LooksLikeModel(modelPath)}");
        Say($"         took {clock.Elapsed.TotalSeconds:0.0} s");

        var inputs = Directory.EnumerateFiles(folder)
            .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Say($"inputs:  {inputs.Count}");

        foreach (var input in inputs)
        {
            Say("");
            Say("== " + Path.GetFileName(input));
            bool video = input.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            var progress = new List<string>();
            var ctx = new ActionContext
            {
                FFmpeg = ff, Settings = settings,
                ProgressChanged = (f, text) => { lock (progress) progress.Add($"{f:0.00} {text}"); },
            };
            var action = TranscribeOps.Action(video ? "vid.transcribe" : "aud.transcribe", video);
            clock.Restart();
            var result = await Task.Run(() => ActionRunner.RunAsync(action, new[] { input }, ctx));
            Say($"took {clock.Elapsed.TotalSeconds:0.0} s, ok {result.Ok}, cancelled {result.Cancelled}");
            foreach (var e in result.Errors) Say("ERROR: " + e);
            foreach (var n in result.Notes) Say("note: " + n.Replace("\n", " | "));
            lock (progress) Say($"progress updates: {progress.Count}: " + string.Join(" ; ", progress.Take(4).Concat(progress.Skip(Math.Max(4, progress.Count - 3)))));
            foreach (var o in result.Outputs)
            {
                Say("output: " + Path.GetFileName(o));
                Say(File.ReadAllText(o).Trim());
                var srt = Path.ChangeExtension(o.Replace("-transcript", ""), ".srt");
                if (video && File.Exists(srt))
                {
                    Say("srt: " + Path.GetFileName(srt));
                    Say(File.ReadAllText(srt).Trim());
                }
            }
        }

        // the log holds whisper's own messages if something failed
        try
        {
            if (File.Exists(Log.FilePath)) File.Copy(Log.FilePath, Path.Combine(folder, "log.txt"), overwrite: true);
        }
        catch { }
        Say("");
        Say("done");
    }
}
