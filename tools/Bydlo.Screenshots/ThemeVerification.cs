using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        services.Theme.Apply(ThemePreference.System);
        var systemBackground = BackgroundColor();
        Require(ThemeService.ForSystemVariant(Avalonia.Platform.PlatformThemeVariant.Dark) == ThemePreference.Oled,
            "System uses Black when the OS is dark");
        Require(ThemeService.ForSystemVariant(Avalonia.Platform.PlatformThemeVariant.Light) == ThemePreference.Light,
            "System uses Light when the OS is light");
        Require(new AppSettings().Theme == ThemePreference.System, "Fresh installs follow the system theme");

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
        Require(!shell.Settings.HasCustomThemeWarning, "Fallback to System clears the warning");
        Require(BackgroundColor() == systemBackground, "Missing custom theme restores the System palette");

        // A theme that cannot be applied must never block startup:
        // ApplyCustom throws, ApplySaved falls back to the System palette.
        services.Theme.Apply(ThemePreference.Purple);
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
        Require(BackgroundColor() == systemBackground, "Fallback applies the System palette");

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
        var rows = shell.Settings.InstalledThemes;
        Require(rows.Count == 2 && rows[1].Id == "ocean" && rows[1].IsActive && rows[1].CanDelete, "Installed theme is listed and active");
        Require(rows[0].Id == SettingsViewModel.BuiltInPurpleId && !rows[0].CanDelete && !rows[0].IsActive,
            "Built-in Purple leads the custom group and cannot be deleted");
        Require(!shell.Settings.HasCustomThemeWarning, "Readable theme shows no warning in Settings");

        var window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-custom.png")); }
        window.Close();

        // Purple lives in the custom group (issue #134): applying it keeps the stored "Purple" value.
        shell.Settings.ApplyCustomThemeCommand.Execute(SettingsViewModel.BuiltInPurpleId);
        Require(shell.Settings.Theme == ThemePreference.Purple && services.Settings.Theme == ThemePreference.Purple,
            "Built-in Purple applies the Purple preference");
        Require(BackgroundColor() == purpleBackground, "Built-in Purple applies the Purple palette");
        Require(shell.Settings.InstalledThemes[0].IsActive && shell.Settings.IsCustomGroupActive, "Purple is marked active in the custom group");
        shell.Settings.DeleteCustomThemeCommand.Execute(SettingsViewModel.BuiltInPurpleId);
        Require(shell.Settings.Theme == ThemePreference.Purple && shell.Settings.InstalledThemes.Count == 2, "Built-in Purple cannot be deleted");
        window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        var baseOptions = window.GetVisualDescendants().OfType<RadioButton>().Count(r => r.GroupName == "theme");
        Require(baseOptions == 3, "Base theme switch offers Light, Black and System only");
        var expander = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "CustomThemesExpander");
        Require(expander.IsExpanded, "Custom group opens when Purple is active");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-purple-custom.png")); }
        window.Close();
        Loc.Instance.SetLanguage("en");
        Require(shell.Settings.InstalledThemes[0].Name == "Purple", "Built-in Purple name follows the UI language");
        Loc.Instance.SetLanguage("ru");
        Require(shell.Settings.InstalledThemes[0].Name == "Фиолетовая", "Built-in Purple name follows the UI language");
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
