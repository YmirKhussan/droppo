using System;
using System.IO;
using System.Text.Json;

namespace Droppo.Core;

/// <summary>User settings, stored as JSON in %AppData%\Droppo\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Last size picked for "Smaller": a number plus whether it's a % of the original or MB.</summary>
    public double CompressValue { get; set; } = 50;
    public bool CompressIsPercent { get; set; } = true;

    /// <summary>Where results go: "SameFolder" (next to the original), "Folder" (OutputFolder) or "Ask".</summary>
    public string OutputMode { get; set; } = "SameFolder";

    /// <summary>Used when OutputMode is "Folder".</summary>
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Droppo");

    /// <summary>Key you hold while dragging to open the wheel: Shift, Ctrl or Alt.</summary>
    public string TriggerKey { get; set; } = "Shift";

    /// <summary>Optional full path to ffmpeg.exe. Leave empty to auto-detect.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Optional full path to qpdf.exe (PDF merge/split/rotate). Leave empty to auto-detect.</summary>
    public string? QpdfPath { get; set; }

    /// <summary>Optional full path to LibreOffice's soffice.exe, used when Microsoft Office isn't installed.</summary>
    public string? LibreOfficePath { get; set; }

    /// <summary>Resolution for PDF pages turned into JPG/PNG.</summary>
    public int PdfImageDpi { get; set; } = 150;

    /// <summary>Video to GIF only uses the first N seconds, so GIFs don't explode in size.</summary>
    public int GifMaxSeconds { get; set; } = 20;

    /// <summary>Long edge (pixels) for images placed into a PDF. 2480 = A4 at 300 dpi.</summary>
    public int PdfMaxImageEdge { get; set; } = 2480;

    /// <summary>Frosted-glass window backgrounds (Windows 11). Off = plain colours.</summary>
    public bool GlassEffects { get; set; } = true;

    /// <summary>Short animations (wheel opening, hover, notifications).</summary>
    public bool Animations { get; set; } = true;

    /// <summary>Speech model for Transcribe: "base" (quick, ~148 MB) or "small" (more accurate, ~488 MB). Empty = not chosen yet.</summary>
    public string TranscribeModel { get; set; } = "";

    /// <summary>Optional full path to whisper-cli.exe. Leave empty to use the one Droppo ships with.</summary>
    public string? WhisperPath { get; set; }

    /// <summary>The "how to use it" window has been shown once.</summary>
    public bool WelcomeShown { get; set; }

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Droppo");

    /// <summary>Where the app kept its files when it was still called CursorConvert.</summary>
    private static string OldFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CursorConvert");

    private static readonly string OldDefaultOutput =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "CursorConvert");

    /// <summary>First run after the rename: bring settings, signature and rename-undo over from the old folder.</summary>
    private static void MigrateFromOldName()
    {
        try
        {
            if (Directory.Exists(Folder) || !Directory.Exists(OldFolder)) return;
            Directory.CreateDirectory(Folder);
            foreach (var name in new[] { "settings.json", "signature.json", "last-rename.json" })
            {
                var from = Path.Combine(OldFolder, name);
                if (File.Exists(from)) File.Copy(from, Path.Combine(Folder, name));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't bring over the old CursorConvert settings: " + ex.Message);
        }
    }

    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        MigrateFromOldName();
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                if (loaded != null)
                {
                    loaded.Sanitize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not read settings, using defaults: " + ex.Message);
        }

        var fresh = new Settings();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            Log.Write("Could not save settings: " + ex.Message);
        }
    }

    private void Sanitize()
    {
        if (CompressValue <= 0 || double.IsNaN(CompressValue) || (CompressIsPercent && CompressValue >= 100)) { CompressValue = 50; CompressIsPercent = true; }
        if (GifMaxSeconds < 1) GifMaxSeconds = 20;
        if (PdfMaxImageEdge < 300) PdfMaxImageEdge = 2480;
        if (PdfImageDpi < 50 || PdfImageDpi > 600) PdfImageDpi = 150;
        if (string.IsNullOrWhiteSpace(TriggerKey)) TriggerKey = "Shift";
        if (OutputMode is not ("SameFolder" or "Folder" or "Ask")) OutputMode = "SameFolder";
        if (string.Equals(OutputFolder, OldDefaultOutput, StringComparison.OrdinalIgnoreCase))
            OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Droppo");
    }
}
