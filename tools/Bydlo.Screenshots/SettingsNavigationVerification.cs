using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

internal static class SettingsNavigationVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        shell.Settings.ReduceMotion = true;
        var view = new SettingsView { DataContext = shell.Settings };
        var window = new Window { Width = 1180, Height = 900, Content = view };
        window.Show();
        var scroll = view.FindControl<ScrollViewer>("SettingsScroll")!;
        var content = view.FindControl<StackPanel>("SettingsSections")!;
        var categories = view.FindControl<WrapPanel>("CategoryButtons")!.Children.OfType<Button>().ToArray();
        var picker = view.FindControl<ComboBox>("CompactCategory")!;
        foreach (var language in new[] { "ru", "en" })
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        foreach (var width in new[] { 1180, 380 })
        {
            Loc.Instance.SetLanguage(language);
            shell.Services.Theme.Apply(theme);
            window.Width = width;
            Pump();
            // Go backwards too: sections already visible must still move to the top.
            foreach (var index in Enumerable.Range(0, categories.Length).Concat(Enumerable.Range(0, categories.Length).Reverse()))
            {
                var button = categories[index];
                var section = view.FindControl<Control>((string)button.Tag!)!;
                if (width >= 660)
                {
                    var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                }
                else
                {
                    picker.SelectedIndex = index;
                }
                Pump();
                var top = section.TranslatePoint(default, content)!.Value.Y - scroll.Offset.Y;
                Require(Math.Abs(top) <= 1, $"{button.Tag} aligns at top ({width}, {language}, {theme}): {top}");
                Require(picker.SelectedIndex == index, "Compact picker retains the current section");
                Require(categories.Count(b => b.Classes.Contains("active")) == 1 && button.Classes.Contains("active"), "Exactly the current category is highlighted");
                if (section is Expander disclosure) { Require(disclosure.IsExpanded, "Destination disclosure opens"); }
            }
            var desktop = view.FindControl<Control>("DesktopSection")!;
            scroll.Offset = new Vector(0, desktop.TranslatePoint(default, content)!.Value.Y + 20);
            Pump();
            Require(picker.SelectedIndex == 2 && categories[2].Classes.Contains("active"), "Manual scrolling updates both indexes");
            using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, $"settings-{language}-{theme}-{width}.png")); }
        }
        window.Close();
        Require(shell.Services.IsMock, "Verification stays in Mock Mode");
        Console.WriteLine("Settings navigation passed: all sections, both directions, top alignment, active indexes, manual scroll, ru/en, Light/Black, wide/narrow.");
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
