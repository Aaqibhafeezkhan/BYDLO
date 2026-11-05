using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

internal static class AutomaticUpdateVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        shell.Settings.ReduceMotion = true;
        foreach (var language in new[] { "ru", "en" })
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        foreach (var width in new[] { 1180, 380 })
        {
            shell.Settings.Language = language;
            Loc.Instance.SetLanguage(language);
            shell.Settings.Theme = theme;
            shell.Services.Theme.Apply(theme);
            Pump();
            var view = new SettingsView { DataContext = shell.Settings };
            var window = new Window { Width = width, Height = 900, Content = view };
            window.Show();
            Pump();
            var picker = view.FindControl<ComboBox>("CompactCategory")!;
            picker.SelectedIndex = 0;
            Pump();
            picker.SelectedIndex = picker.Items.Count - 1;
            Pump();
            var toggle = view.FindControl<ToggleSwitch>("AutoUpdateToggle")!;
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(toggle))) { throw new InvalidOperationException("Missing accessible update label"); }
            if (!toggle.IsEffectivelyVisible || toggle.Bounds.Width <= 0) { throw new InvalidOperationException("Update toggle is hidden"); }
            shell.Settings.AutoUpdateApp = false;
            toggle.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Pump();
            if (!shell.Services.Settings.AutoUpdateApp) { throw new InvalidOperationException("Keyboard opt-in failed"); }
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Pump();
            if (shell.Services.Settings.AutoUpdateApp) { throw new InvalidOperationException("Keyboard opt-out failed"); }
            shell.Settings.AutoUpdateApp = true;
            shell.Settings.CheckForUpdatesIfDueAsync().GetAwaiter().GetResult();
            if (shell.Services.Settings.LastAutomaticUpdateCheck is not null) { throw new InvalidOperationException("Mock must block automatic downloads"); }
            shell.Settings.AutoUpdateApp = false;
            Pump();
            if (Loc.Instance.Language != language) { throw new InvalidOperationException("Verification language changed unexpectedly"); }
            if (!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Loc.Instance["settings.update.auto"]))
            { throw new InvalidOperationException("Update label did not render in the requested language"); }
            using var frame = window.CaptureRenderedFrame();
            frame!.Save(Path.Combine(output, $"auto-update-{language}-{theme}-{width}.png"));
            window.Close();
        }
        Console.WriteLine("Automatic update UI passed: keyboard toggle/persistence, Mock guard, RU/EN, Light/Black, wide/narrow.");
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }
}
