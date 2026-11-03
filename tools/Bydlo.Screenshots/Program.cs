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
if (args.Contains("--verify-simple-ui", StringComparer.Ordinal) || args.Contains("--verify-wizard", StringComparer.Ordinal) || args.Contains("--verify-backup", StringComparer.Ordinal) || args.Contains("--verify-import", StringComparer.Ordinal) || args.Contains("--verify-share", StringComparer.Ordinal) ||
    args.Contains("--verify-themes", StringComparer.Ordinal) || args.Contains("--verify-active-connections", StringComparer.Ordinal) || args.Contains("--verify-routing-picker", StringComparer.Ordinal) ||
    args.Contains("--verify-zapret-selection", StringComparer.Ordinal) || args.Contains("--verify-desktop", StringComparer.Ordinal) || args.Contains("--verify-bundled-engines", StringComparer.Ordinal) ||
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

if (args.Contains("--export-brand-icon", StringComparer.Ordinal))
{
    BrandIconExport.Run(output);
    return;
}

if (args.Contains("--verify-wizard", StringComparer.Ordinal))
{
    WizardVerification.Run(services, output);
    return;
}
var shell = new MainViewModel(services);
if (args.Contains("--verify-themes", StringComparer.Ordinal))
{
    ThemeVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-backup", StringComparer.Ordinal))
{
    BackupVerification.Run(shell, output);
    return;
}
if (args.Contains("--verify-bundled-engines", StringComparer.Ordinal))
{
    BundledEnginesVerification.Run(shell, services, output);
    return;
}
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

void Shoot(string name, double width, double height, Action<Window>? arrange = null)
{
    var window = new Window { Width = width, Height = height, Content = new MainView { DataContext = shell } };
    window.Show();
    Pump(400);
    if (arrange is not null)
    {
        arrange(window);
        Pump(150);
    }
    using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No rendered frame: " + name);
    frame.Save(Path.Combine(output, name + ".png"));
    window.Close();
    Console.WriteLine($"saved {name}.png");
}

if (args.Contains("--preview-brand", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    Bydlo.App.Services.Loc.Instance.SetLanguage(lang);
    services.Theme.Apply(ThemePreference.Purple);
    Shoot("brand-desktop", 1180, 780);
    Shoot("brand-compact", 380, 700);
    return;
}

if (args.Contains("--verify-simple-ui", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    if (shell.Profiles.Count != 0) throw new InvalidOperationException("Use a fresh output directory for empty-state verification.");
    Bydlo.App.Services.Loc.Instance.SetLanguage(lang);
    foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled, ThemePreference.System })
    {
        services.Theme.Apply(theme);
        foreach (var (width, height) in new[] { (1180, 780), (900, 780), (760, 780), (380, 700) })
        {
            shell.Navigate("home");
            Shoot($"simple-home-{theme.ToString().ToLowerInvariant()}-{width}", width, height);
        }
    }
    services.Theme.Apply(ThemePreference.Purple);
    foreach (var source in new[] { "link", "subscription", "clipboard", "file", "qr" })
    {
        // No native pickers or clipboard in headless mode: their unavailable-service errors
        // must leave the wizard open on the correct source page, never start an engine.
        services.TopLevel = () => null;
        Wait(shell.OpenImportCommand.ExecuteAsync(source));
        var expected = source == "file" ? AddServerStep.File : source == "qr" ? AddServerStep.Qr : AddServerStep.Link;
        if (!shell.IsAddServerOpen || shell.AddServerWizard.Step != expected)
            throw new InvalidOperationException("Wrong direct import destination: " + source);
        shell.AddServerWizard.CloseCommand.Execute(null);
    }
    Wait(shell.OpenImportCommand.ExecuteAsync("link"));
    shell.AddServerWizard.Input = "draft";
    Wait(shell.OpenImportCommand.ExecuteAsync("subscription"));
    if (shell.AddServerWizard.Input != "draft") throw new InvalidOperationException("Direct import reset an open wizard.");
    shell.AddServerWizard.CloseCommand.Execute(null);
    var settingsWindow = new Window { Width = 380, Height = 700, Content = new SettingsView { DataContext = shell.Settings } };
    settingsWindow.Show();
    Pump(100);
    var advanced = settingsWindow.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "AdvancedSettings");
    if (advanced.IsExpanded) throw new InvalidOperationException("Advanced settings must start collapsed.");
    advanced.IsExpanded = true;
    Pump(50);
    if (!shell.Settings.ShowAdvanced || !services.Settings.ShowAdvanced)
        throw new InvalidOperationException("Advanced disclosure did not update the saved preference.");
    advanced.IsExpanded = false;
    Pump(50);
    if (shell.Settings.ShowAdvanced) throw new InvalidOperationException("Advanced disclosure did not close.");
    settingsWindow.Close();
    foreach (var page in new[] { "tools", "settings" })
    {
        shell.Navigate(page);
        Shoot($"simple-{page}", 1180, 900);
        Shoot($"simple-{page}-380", 380, 700);
    }
    shell.Navigate("home");
    var homeWindow = new Window { Width = 1180, Height = 780, Content = new MainView { DataContext = shell } };
    homeWindow.Show();
    Pump(100);
    var toggles = homeWindow.GetVisualDescendants().OfType<ToggleSwitch>()
        .Where(c => c.IsEffectivelyVisible && (Avalonia.Automation.AutomationProperties.GetName(c) is "Zapret" or "TgWsProxy")).ToArray();
    if (toggles.Length != 1 || Avalonia.Automation.AutomationProperties.GetName(toggles[0]) != "Zapret")
        throw new InvalidOperationException("Only Zapret should use a toggle.");
    var telegramButton = homeWindow.GetVisualDescendants().OfType<Button>()
        .Single(b => ReferenceEquals(b.Command, shell.Tools.OpenInTelegramCommand));
    if (!telegramButton.IsEffectivelyVisible || !telegramButton.IsEnabled)
        throw new InvalidOperationException("Telegram connection must be available before the proxy starts.");
    homeWindow.Width = 380;
    Pump(100);
    var shortcut = homeWindow.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ToolShortcut");
    if (!shortcut.IsEffectivelyVisible || !ReferenceEquals(shortcut.Command, shell.NavigateCommand))
        throw new InvalidOperationException("Compact home must expose the tool shortcut.");
    homeWindow.Close();
    Wait(shell.Home.ToggleZapretCommand.ExecuteAsync(null));
    Pump(50); // Session notifications are posted to the UI dispatcher.
    if (!shell.Home.ZapretOn) throw new InvalidOperationException("Mock Zapret tool did not start.");
    Wait(shell.Home.ToggleZapretCommand.ExecuteAsync(null));
    Pump(50); // Session notifications are posted to the UI dispatcher.
    if (shell.Home.ZapretOn) throw new InvalidOperationException("Mock Zapret tool did not stop.");
    // No OS launcher in headless mode: exercise startup and the missing-Telegram fallback safely.
    services.TopLevel = () => null;
    Wait(shell.Tools.OpenInTelegramCommand.ExecuteAsync(null));
    Pump(50);
    if (!shell.Home.TgOn) throw new InvalidOperationException("Mock Telegram tool did not start.");
    var telegramStarted = services.Connections.TgWsProxy.ConnectedSince;
    Wait(shell.Tools.OpenInTelegramCommand.ExecuteAsync(null));
    if (services.Connections.TgWsProxy.ConnectedSince != telegramStarted)
        throw new InvalidOperationException("Opening Telegram twice restarted the proxy.");
    Shoot("simple-home-telegram-enabled", 1180, 780);
    Wait(services.Connections.StopAllAsync());
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
    Shoot("simple-home-with-server", 1180, 780);
    Shoot("simple-home-with-server-380", 380, 700);
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    Shoot("simple-home-connected", 1180, 780);
    Wait(services.Connections.StopAllAsync());
    Console.WriteLine("Simple UI verification passed: themes, compact layout, direct import destinations, independent mock tools and VPN.");
    return;
}

if (args.Contains("--verify-routing-picker", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    if (OperatingSystem.IsWindows())
    {
        shell.Home.IsTun = true;
        shell.Navigate("routing");
        var routing = shell.Routing;
        routing.ShowRuleEditor = true;
        Wait(routing.FindApplicationsCommand.ExecuteAsync(null));
        var application = routing.MatchingApplications.First();
        routing.ApplicationSearch = application.Name;
        routing.SelectedApplication = routing.MatchingApplications.First();
        routing.AddApplicationCommand.Execute(null);
        routing.ProxyOnlyListedProcesses = true;
        Wait(routing.SaveCommand.ExecuteAsync(null));
        if (!services.Settings.Routing.DirectProcesses.Contains(application.Name) || !services.Settings.Routing.ProxyOnlyListedProcesses)
        {
            throw new InvalidOperationException("The selected Windows application or routing mode was not saved.");
        }
        foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
        {
            services.Theme.Apply(theme);
            foreach (var (width, height) in new[] { (1180, 900), (380, 700) })
            {
                Shoot($"routing-application-picker-{theme.ToString().ToLowerInvariant()}-{width}", width, height,
                    window => window.GetVisualDescendants().OfType<ComboBox>()
                        .Single(control => control.Name == "ApplicationPicker").BringIntoView());
            }
        }
        Console.WriteLine("Routing picker verification passed: Windows catalog, filtering, selection and saving.");
    }
    return;
}

if (args.Contains("--verify-active-connections", StringComparer.Ordinal))
{
    shell.FinishOnboardingCommand.Execute(null);
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    shell.Navigate("statistics");
    var stats = shell.Statistics;
    stats.ActiveConnectionsExpanded = true;
    Pump(200);
    var connection = stats.FilteredConnections.Single();
    stats.ConnectionFilter = "missing";
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Filter did not hide the mock connection."); }
    stats.ConnectionFilter = "browser";
    if (stats.FilteredConnections.Single() != connection) { throw new InvalidOperationException("Filter lost the matching connection."); }
    foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
    {
        services.Theme.Apply(theme);
        Shoot($"active-connections-{theme.ToString().ToLowerInvariant()}", 1180, 900);
        Shoot($"active-connections-{theme.ToString().ToLowerInvariant()}-minimum", 380, 700,
            window => window.GetVisualDescendants().OfType<Expander>().Single().BringIntoView());
    }
    Wait(stats.CloseConnectionCommand.ExecuteAsync(connection));
    if (stats.FilteredConnections.Any()) { throw new InvalidOperationException("Mock connection remained after closing it."); }
    stats.AddConnectionRuleCommand.Execute(connection);
    if (shell.Routing.RuleValue != "example.invalid" || !shell.Routing.ShowRuleEditor)
    {
        throw new InvalidOperationException("Connection rule was not prefilled.");
    }
    Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
    Console.WriteLine("Active connections verification passed: mock list, filter, close, prefilled routing rule.");
    return;
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
