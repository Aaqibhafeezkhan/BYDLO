using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;
using Bydlo.Core.Themes;

internal static class ThemeVerification
{
    // Real Avalonia app with the real ThemeService (headless). No engines, no network.
    public static void Run(MainViewModel shell, string output)
    {
        var services = shell.Services;
        var themesDir = CustomThemeStore.ThemesDirectory(services.Paths.DataDirectory);
        services.Theme.Apply(ThemePreference.Purple);
        var purpleBackground = BackgroundColor();

        var same = CustomTheme.Parse("""
            {"id": "same", "name": "Same", "author": "", "version": 1, "base": "dark",
             "colors": {"Text": "#112233", "Bg": "#112233", "Surface": "#112233"}}
            """);
        Require(services.Theme.ApplyCustom(same), "Identical text and background trigger the contrast warning");
        Require(ThemeService.HasLowContrast(Avalonia.Application.Current!), "Warning is visible through merged dictionaries");
        services.Settings.Theme = ThemePreference.Custom;
        services.Settings.CustomThemeId = "missing";
        Require(!services.Theme.ApplySaved(services.Settings, services.Paths.DataDirectory), "Missing custom file reports no warning");
        shell.Settings.RefreshCustomThemes();
        Require(!shell.Settings.HasCustomThemeWarning, "Fallback to Purple clears the warning");
        Require(BackgroundColor() == purpleBackground, "Missing custom theme restores the Purple palette");

        // A theme that cannot be applied must never block startup:
        // ApplyCustom throws, ApplySaved falls back to the Purple base palette.
        services.Theme.Apply(ThemePreference.Light);
        Require(BackgroundColor() != purpleBackground, "Light has a different background from Purple");
        var bad = new CustomThemeDocument("bad", "Bad", "", 1, true,
            new Dictionary<string, string> { ["Text"] = "#112233\n" }, []);
        try
        {
            services.Theme.ApplyCustom(bad);
            throw new InvalidOperationException("Unappliable color was applied");
        }
        catch (FormatException) { }
        Require(!services.Theme.ApplySaved(new AppSettings { Theme = ThemePreference.Custom, CustomThemeId = "missing" }, services.Paths.DataDirectory),
            "Unappliable custom theme falls back without throwing");
        Require(BackgroundColor() == purpleBackground, "Fallback applies the Purple base palette");

        var ocean = CustomTheme.Parse("""
            {"id": "ocean", "name": "Ocean", "author": "BYDLO", "version": 1, "base": "dark",
             "colors": {"Bg": "#0F1A24", "Surface": "#15222E", "Primary": "#2E8FB8", "Accent": "#5EC8E5", "Text": "#E8F1F5"}}
            """);
        CustomThemeStore.Save(themesDir, """
            {"id": "ocean", "name": "Ocean", "author": "BYDLO", "version": 1, "base": "dark",
             "colors": {"Bg": "#0F1A24", "Surface": "#15222E", "Primary": "#2E8FB8", "Accent": "#5EC8E5", "Text": "#E8F1F5"}}
            """);
        Require(!services.Theme.ApplyCustom(ocean), "Readable theme applies without a warning");
        services.Settings.CustomThemeId = "ocean";
        shell.Settings.Theme = ThemePreference.Custom;
        Require(shell.Settings.InstalledThemes.Count == 1 && shell.Settings.InstalledThemes[0].IsActive, "Installed theme is listed and active");
        Require(!shell.Settings.HasCustomThemeWarning, "Readable theme shows no warning in Settings");

        var window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-custom.png")); }
        window.Close();
        services.Settings.Theme = ThemePreference.Purple;
        services.Settings.CustomThemeId = null;
        services.Theme.Apply(ThemePreference.Purple);
        Console.WriteLine("Theme verification passed: real-service contrast warning, fallback, install, Settings render.");
    }

    private static Color BackgroundColor() =>
        Avalonia.Application.Current!.Resources.TryGetResource("BgBrush", null, out var resource) &&
        resource is ISolidColorBrush brush
            ? brush.Color
            : throw new InvalidOperationException("The active theme must provide a solid background brush.");

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
