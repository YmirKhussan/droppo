using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Droppo.Core;

public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>Runs a command-line tool (ffmpeg, qpdf) quietly and turns failures into readable errors.</summary>
public static class ProcessRunner
{
    /// <summary>For tools that answer yes/no through their exit code (e.g. "is this PDF encrypted?").</summary>
    public static async Task<int> ExitCodeAsync(string exe, IEnumerable<string> args, CancellationToken ct) =>
        (await CaptureAsync(exe, args, ct).ConfigureAwait(false)).ExitCode;

    /// <summary>Runs and returns everything without judging success (for probing files).</summary>
    public static async Task<(int ExitCode, string Output, string Error)> CaptureAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi) ?? throw new ToolException($"Could not start {Path.GetFileName(exe)}.");
        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
    }

    public static async Task<string> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct,
        Func<int, bool>? isSuccess = null, Func<string, string?>? friendly = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new ToolException($"Could not start {Path.GetFileName(exe)}.");
        using var kill = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        });

        var errTask = proc.StandardError.ReadToEndAsync();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var err = await errTask.ConfigureAwait(false);
        var output = await outTask.ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        bool ok = isSuccess?.Invoke(proc.ExitCode) ?? proc.ExitCode == 0;
        if (ok) return output;

        var text = string.IsNullOrWhiteSpace(err) ? output : err;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tail = string.Join(" | ", lines.TakeLast(3));
        Log.Write($"{Path.GetFileName(exe)} failed ({proc.ExitCode}): {string.Join(' ', psi.ArgumentList)}{Environment.NewLine}  {text.Trim()}");
        throw new ToolException(friendly?.Invoke(text)
            ?? (string.IsNullOrWhiteSpace(tail) ? $"{Path.GetFileName(exe)} exited with code {proc.ExitCode}" : tail));
    }
}

/// <summary>Finds a tool in settings, next to the app, on PATH, or where winget/scoop/choco put it.</summary>
public static class ToolLocator
{
    public static string? Locate(string baseName, string? configured, IEnumerable<string>? extra = null)
    {
        var exe = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        foreach (var candidate in Candidates(exe, configured, extra ?? Array.Empty<string>()))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // malformed path, skip
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates(string exe, string? configured, IEnumerable<string> extra)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured.Trim().Trim('"');

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, exe);
        yield return Path.Combine(baseDir, "tools", exe);
        // the installer bundles each tool in its own folder (their DLLs must not mix): tools\ffmpeg\ffmpeg.exe, tools\qpdf\qpdf.exe
        yield return Path.Combine(baseDir, "tools", Path.GetFileNameWithoutExtension(exe), exe);

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(dir.Trim().Trim('"'), exe);

        if (!OperatingSystem.IsWindows()) yield break;

        // winget adds a link here, but our process may have started before PATH was refreshed
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exe);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "scoop", "shims", exe);
        yield return @"C:\ProgramData\chocolatey\bin\" + exe;

        foreach (var e in extra) yield return e;
    }

    /// <summary>Finds exe anywhere under root\dirPattern (e.g. "C:\Program Files", "qpdf*").</summary>
    public static IEnumerable<string> SearchUnder(string root, string dirPattern, string exe)
    {
        var results = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return results;
            foreach (var dir in Directory.EnumerateDirectories(root, dirPattern))
                results.AddRange(Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories));
        }
        catch
        {
            // permission problems etc
        }
        return results.OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase); // newest version folder first
    }
}
