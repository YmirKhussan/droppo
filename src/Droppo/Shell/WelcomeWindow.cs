using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Droppo.Shell.Studio;

namespace Droppo.Shell;

/// <summary>Shown once after installing: a tray app is invisible, so say where it is and how to use it.</summary>
internal static class WelcomeWindow
{
    public static void Show(string triggerKey, Action tryIt)
    {
        var w = Ui.Dialog("Welcome to Droppo", owner: null);
        w.ShowInTaskbar = true;
        var panel = new StackPanel { Margin = new Thickness(30, 26, 30, 24), Width = 460 };

        panel.Children.Add(Brand.Lockup(34));
        var headline = Ui.Txt(Brand.Tagline, 24, bold: true);
        headline.Margin = new Thickness(0, 22, 0, 0);
        panel.Children.Add(headline);
        panel.Children.Add(Para("Droppo is running. There's no window to open: it sits by the clock and waits for you to drag a file. " +
                                "Can't see the icon? Click the ^ arrow next to the clock.", Ui.Muted));

        var steps = new StackPanel();
        steps.Children.Add(Step("1", "Start dragging a file (or a few) in File Explorer or on the desktop."));
        steps.Children.Add(Step("2", $"While dragging, press and hold {triggerKey}. The wheel appears."));
        steps.Children.Add(Step("3", "Let go on what you want. The result is saved next to your file, and your original is never changed."));
        var card = Ui.CardPanel(steps, new Thickness(16, 10, 16, 14));
        card.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(card);

        panel.Children.Add(Para("Click the tray icon for Settings, or right-click it for PDF Studio. " +
                                "Opening Droppo from the Start menu again also brings up Settings.", Ui.Muted));

        panel.Children.Add(Ui.ButtonRow(
            Ui.Btn(null, "Try it now", () => { w.Close(); tryIt(); }),
            Ui.Btn(null, "Got it", () => w.Close(), primary: true)));

        w.Content = panel;
        w.Loaded += (_, _) => w.Activate();
        w.Show();
    }

    private static TextBlock Para(string text, Brush color) => new()
    {
        Text = text, FontSize = 13, Foreground = color, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8),
    };

    private static UIElement Step(string number, string text)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var badge = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Ui.AccentSoft,
            Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Accent,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        row.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        return row;
    }
}
