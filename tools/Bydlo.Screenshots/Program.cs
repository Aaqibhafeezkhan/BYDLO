using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
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
if (Environment.GetEnvironmentVariable("BYDLO_DATA_DIR") is null)
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

if (shell.Profiles.Count == 0)
{
    Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
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
    var frame = window.CaptureRenderedFrame();
    frame?.Save(Path.Combine(output, name + ".png"));
    window.Close();
    Console.WriteLine($"saved {name}.png");
}

foreach (var theme in new[] { ThemePreference.Purple, ThemePreference.Light, ThemePreference.Oled })
{
    services.Theme.Apply(theme);
    shell.Navigate("home");
    Shoot($"home-{theme.ToString().ToLowerInvariant()}", 1180, 780);
}

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
}

shell.Navigate("home");
Shoot("mobile-home", 400, 860);
Wait(services.Connections.StopAllAsync());
Console.WriteLine("done: " + output);
