using System;
using System.IO;

namespace Droppo.Core;

/// <summary>Tiny append-only log at %AppData%\Droppo\log.txt. Paste this when asking for help.</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 1_000_000)
                {
                    File.Delete(FilePath); // keep it small, it's a debug log not an archive
                }
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
