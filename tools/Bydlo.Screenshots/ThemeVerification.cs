using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;
using Bydlo.Core.Themes;

internal static class ThemeVerification
{
    // Real Avalonia app with the real ThemeService (headless). No engines, no network.
    public static void Run(MainViewModel shell, string output)
    {
        var services = shell.Services;
        Require(shell.IsOnboardingVisible && shell.OnboardingThemeSystem, "Fresh installation starts with onboarding and System selected");
        VerifyThemeChoices(shell, output);
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
        Require(shell.Settings.InstalledThemes.Count == 1 && shell.Settings.InstalledThemes[0].IsActive, "Installed theme is listed and active");
        Require(!shell.Settings.HasCustomThemeWarning, "Readable theme shows no warning in Settings");

        var window = new Window { Width = 1180, Height = 900, Content = new SettingsView { DataContext = shell.Settings } };
        window.Show();
        Pump();
        Require(window.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "CustomThemesExpander").IsExpanded,
            "Custom group opens when a custom theme is active");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-custom.png")); }
        window.Close();

        // Purple ships only as the gallery file themes/purple.json (issue #134); it must reproduce the old palette.
        services.Theme.Apply(ThemePreference.Purple);
        var purplePalette = Palette();
        var purple = CustomThemeStore.Save(themesDir, File.ReadAllText(RepositoryFile("themes", "purple.json")));
        Require(purple.Id == "purple" && purple.IgnoredKeys.Count == 0, "Gallery Purple parses without ignored keys");
        shell.Settings.ApplyCustomThemeCommand.Execute("purple");
        Require(shell.Settings.Theme == ThemePreference.Custom && services.Settings.CustomThemeId == "purple", "Gallery Purple installs as a custom theme");
        Require(Palette().SequenceEqual(purplePalette), "Gallery Purple reproduces the former built-in palette");
        Require(BackgroundColor() == purpleBackground, "Gallery Purple applies the Purple background");
        shell.Settings.DeleteCustomThemeCommand.Execute("purple");
        Require(shell.Settings.Theme == ThemePreference.System && BackgroundColor() == systemBackground, "Deleting gallery Purple returns to System");

        services.Settings.Theme = ThemePreference.System;
        services.Settings.CustomThemeId = null;
        services.Theme.Apply(ThemePreference.System);
        Console.WriteLine("Theme verification passed: first-run System, legacy Purple migration, gallery Purple palette, Black, contrast warning, fallback, install, Settings render.");
    }

    // Ported from PR #173: legacy settings for every theme, single selection, Black palette, first run.
    private static void VerifyThemeChoices(MainViewModel shell, string output)
    {
        var services = shell.Services;
        var view = new SettingsView { DataContext = shell.Settings };
        var window = new Window { Width = 1180, Height = 900, Content = view };
        window.Show();
        Pump();
        var radios = view.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "theme").ToArray();
        Require(radios.Length == 3, "Base theme switch offers Light, Black and System only");
        Require(radios.Any(r => r.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Loc.T("theme.oled"))), "Black is available");
        foreach (var preference in Enum.GetValues<ThemePreference>().Where(t => t != ThemePreference.Custom))
        {
            // Settings written by older versions: Purple is no longer built in and migrates to System.
            var legacyPath = Path.Combine(output, "legacy-settings.json");
            File.WriteAllText(legacyPath, "{\"Theme\":\"" + preference + "\"}");
            var saved = new SettingsStore(legacyPath).LoadAsync().GetAwaiter().GetResult();
            var expected = preference == ThemePreference.Purple ? ThemePreference.System : preference;
            Require(saved.Theme == expected && saved.OnboardingCompleted, "Legacy selection loads without onboarding: " + preference);
            services.Theme.ApplySaved(saved, services.Paths.DataDirectory);
            var background = BackgroundColor();
            shell.Settings.Theme = saved.Theme;
            Pump();
            Require(BackgroundColor() == background, "Selecting a legacy theme preserves its palette");
            Require(radios.Count(r => r.IsChecked == true) == 1, "Exactly one base theme is selected");
        }
        shell.Settings.ThemeOled = true;
        Pump();
        Require(BackgroundColor() == Colors.Black, "Black uses a black background");
        window.Close();

        var onboarding = new MainView { DataContext = shell };
        window = new Window { Width = 1180, Height = 900, Content = onboarding };
        window.Show();
        Pump();
        onboarding.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == Loc.T("onboarding.appearance")).IsExpanded = true;
        Pump();
        var onboardingRadios = onboarding.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "onboarding-theme").ToArray();
        Require(onboardingRadios.Length == 3, "First run offers Light, Black and System only");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "themes-onboarding.png")); }
        shell.OnboardingThemeOled = true;
        Pump();
        Require(services.Settings.Theme == ThemePreference.Oled, "First-run Black selection is saved");
        window.Close();
    }

    private static Color[] Palette() =>
        CustomTheme.ResourceKeys.Select(key => Avalonia.Application.Current!.Resources.TryGetResource(key, null, out var value)
            ? value switch { ISolidColorBrush b => b.Color, Color c => c, _ => default }
            : throw new InvalidOperationException("Missing palette key " + key)).ToArray();

    private static string RepositoryFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(path)) { return path; }
        }
        throw new FileNotFoundException("Run from the repository checkout.", Path.Combine(parts));
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
