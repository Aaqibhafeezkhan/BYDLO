using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;

namespace Bydlo.App;

public sealed partial class App : Application
{
    /// <summary>Command-line arguments, set by the platform head before start.</summary>
    public static string[] Args { get; set; } = [];

    public AppServices? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Loading settings and profiles is fast local file I/O; doing it synchronously keeps startup simple.
        Services = Task.Run(() => AppServices.CreateAsync(Args)).GetAwaiter().GetResult();
        Loc.Instance.SetLanguage(Services.Settings.Language);
        Services.Theme.Apply(Services.Settings.Theme);
        Services.Theme.TrackSystemTheme();
        var shell = new MainViewModel(Services);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = shell };
            desktop.MainWindow = window;
            window.Closing += (_, _) => Services.ShutdownAsync().GetAwaiter().GetResult();
            desktop.Exit += (_, _) => Services.ShutdownAsync().GetAwaiter().GetResult();
            desktop.ShutdownRequested += (_, _) => Services.ShutdownAsync().GetAwaiter().GetResult();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            single.MainView = new MainView { DataContext = shell };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
