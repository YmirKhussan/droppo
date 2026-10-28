using System;
using System.Windows;
using System.Windows.Markup;
using Droppo.Core;

namespace Droppo.Shell;

/// <summary>
/// Loads the control styles (Theme\*.xaml) one file at a time. If one ever fails to load,
/// that control just keeps the standard Windows look and the app carries on.
/// </summary>
internal static class Theme
{
    private static readonly string[] Parts = { "Buttons", "Inputs", "Combo", "Toggles", "Scroll", "Slider", "Menus", "Lists" };

    public static void Load(Application app)
    {
        var assembly = typeof(Theme).Assembly;
        foreach (var part in Parts)
        {
            try
            {
                using var stream = assembly.GetManifestResourceStream($"Droppo.Theme.{part}.xaml");
                if (stream == null) { Log.Write($"Theme part {part} is missing."); continue; }
                if (XamlReader.Load(stream) is ResourceDictionary dictionary)
                    app.Resources.MergedDictionaries.Add(dictionary);
            }
            catch (Exception ex)
            {
                Log.Write($"Theme part {part} didn't load, using the standard look for it: {ex.Message}");
            }
        }
    }
}
