using System;
using Microsoft.Win32;
using Droppo.Core;

namespace Droppo.Shell;

/// <summary>"Start with Windows" via the per-user Run key. No admin rights needed.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Droppo";

    /// <summary>Added to the startup entry so Droppo starts quietly with Windows (no "ready" message).</summary>
    public const string StartupArg = "--startup";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>The app used to be called CursorConvert: swap its old startup entry for the new one.</summary>
    public static void MigrateOldEntry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue("CursorConvert") is not string) return;
            key.DeleteValue("CursorConvert", throwOnMissingValue: false);
            Set(true);
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't update the old startup entry: " + ex.Message);
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" {StartupArg}");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("Could not change start-with-Windows: " + ex.Message);
        }
    }
}
