using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

// Usage: dotnet run --project tools/Bydlo.Screenshots -- <output-dir> [en|ru]
// Forces Mock Mode and the development safety lock; nothing is started and no network is used.
Environment.SetEnvironmentVariable("BYDLO_MOCK", "1");
Environment.SetEnvironmentVariable("BYDLO_SAFE_DEV", "1");
var output = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(".tmp", "screenshots"));
var lang = args.Length > 1 ? args[1] : "en";
Directory.CreateDirectory(output);
if (args.Contains("--verify-import", StringComparer.Ordinal) || args.Contains("--verify-share", StringComparer.Ordinal) ||
    args.Contains("--verify-zapret-selection", StringComparer.Ordinal) || args.Contains("--verify-desktop", StringComparer.Ordinal) ||
    Environment.GetEnvironmentVariable("BYDLO_DATA_DIR") is null)
{
    Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", Path.Combine(output, "appdata"));
}

Bydlo.App.App.Args = ["--mock"];
AppBuilder.Configure<Bydlo.App.App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
    .SetupWithoutStarting();

var app = (Bydlo.App.App)Application.Current!;
if (app.Services is null)
{
    app.OnFrameworkInitializationCompleted();
}
var services = app.Services!;
Bydlo.App.Services.Loc.Instance.SetLanguage(lang);

var shell = new MainViewModel(services);
if (args.Contains("--verify-desktop", StringComparer.Ordinal))
{
    DesktopVerification.Run(shell, services, output);
    return;
}
if (args.Contains("--verify-zapret-selection", StringComparer.Ordinal))
{
    ZapretSelectionVerification.Run(shell, services, output);
    return;
}
if (args.Contains("--verify-share", StringComparer.Ordinal))
{
    ShareVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-import", StringComparer.Ordinal))
{
    ImportVerification.Run(shell, output);
    return;
}

// The UI thread is this thread: never block on a task, pump the dispatcher until it completes.
void Wait(Task task)
{
    while (!task.IsCompleted)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }

    task.GetAwaiter().GetResult();
}

void Pump(int ms)
{
    var until = DateTime.UtcNow.AddMilliseconds(ms);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }
}

void Shoot(string name, double width, double height)
{
    var window = new Window { Width = width, Height = height, Content = new MainView { DataContext = shell } };
    window.Show();
    Pump(400);
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame: " + name);
    frame.Save(Path.Combine(output, name + ".png"));
    window.Close();
    Console.WriteLine($"saved {name}.png");
}

if (shell.IsOnboardingVisible)
{
    Shoot("onboarding", 1180, 780);
    Shoot("mobile-onboarding", 400, 860);
    Shoot("mobile-onboarding-minimum", 380, 560);

    // Verify that Settings picks up choices made during onboarding.
    shell.OnboardingLanguage = "en";
    shell.OnboardingTheme = ThemePreference.Oled;
    shell.FinishOnboardingCommand.Execute(null);
    shell.Navigate("settings");
    Shoot("settings-after-onboarding", 1180, 900);
    shell.OnboardingLanguage = lang;
    shell.OnboardingTheme = ThemePreference.System;
    shell.Navigate("home");
}

Shoot("home-empty", 1180, 780);
Shoot("mobile-home-empty", 400, 860);
if (shell.Profiles.Count == 0)
{
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
}
// Exercise the actual radio group: selecting either mode must update the shared setting.
// This only changes app settings in the isolated screenshot data directory; no engine runs.
var originalMode = services.Settings.Mode;
var modeWindow = new Window { Width = 400, Height = 860, Content = new HomeView { DataContext = shell.Home } };
modeWindow.Show();
Pump(100);
var modes = modeWindow.GetVisualDescendants().OfType<RadioButton>().Where(r => r.GroupName == "connectionMode").ToArray();
if (modes.Length != 2) throw new InvalidOperationException("Connection mode controls are missing.");
modes[1].IsChecked = true;
Pump(50);
if (services.Settings.Mode != ConnectionMode.Tun || shell.Home.IsProxy) throw new InvalidOperationException("Whole-device selection failed.");
modes[0].IsChecked = true;
Pump(50);
if (services.Settings.Mode != ConnectionMode.LocalProxy || shell.Home.IsTun) throw new InvalidOperationException("Proxy selection failed.");
shell.Home.IsTun = originalMode == ConnectionMode.Tun;
modeWindow.Content = new SettingsView { DataContext = shell.Settings };
Pump(100);
var disclosure = modeWindow.GetVisualDescendants().OfType<Expander>().First();
var disclosureButton = disclosure.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("disclosure"));
disclosureButton.IsChecked = true;
Pump(50);
if (!disclosure.IsExpanded) throw new InvalidOperationException("Disclosure did not open.");
disclosureButton.IsChecked = false;
Pump(50);
if (disclosure.IsExpanded) throw new InvalidOperationException("Disclosure did not close.");
modeWindow.Close();
Pump(4000); // Let the demo-import toast disappear before documenting the resting UI.

foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
{
    services.Theme.Apply(theme);
    shell.Navigate("home");
    Shoot($"home-{theme.ToString().ToLowerInvariant()}", 1180, 780);
}

services.Settings.Theme = ThemePreference.Purple;
services.Theme.Apply(ThemePreference.Purple);

// Simulated connection (Mock engine) for the "connected" state.
services.Settings.MockScenario = MockScenario.Success;
Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
Pump(3500);
Shoot("home-connected", 1180, 780);

foreach (var page in new[] { "connections", "tools", "routing", "statistics", "settings" })
{
    shell.Navigate(page);
    if (page == "settings")
    {
        shell.Settings.ShowAdvanced = true;
    }

    Shoot(page, 1180, 900);
    Shoot($"mobile-{page}", 400, 860);
}

shell.Navigate("home");
Shoot("mobile-home", 400, 860);
Shoot("home-minimum", 380, 700);
Shoot("home-narrow-desktop", 900, 780);
Shoot("home-navigation-breakpoint", MainView.CompactWidth, 780);
Wait(services.Connections.StopAllAsync());
Pump(100);
services.Settings.MockScenario = MockScenario.FailOnConnect;
Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
Pump(100);
if (!shell.Home.IsError) throw new InvalidOperationException("Mock connection failure was not shown.");
if (shell.HasVisibleToast && shell.Toast == shell.Home.Error) throw new InvalidOperationException("Duplicate connection error toast.");
Shoot("home-error", 400, 860);
Wait(services.Connections.StopAllAsync());
services.Settings.MockScenario = MockScenario.Success;
Console.WriteLine("done: " + output);
