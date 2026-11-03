using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Threading;
using Bydlo.App.Services;

internal static class StartupErrorVerification
{
    public static void Run(string output, string language)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(language);
        var data = Path.Combine(output, "unused-appdata");
        Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", data);
        Bydlo.App.App.StartupErrorKey = "startup.activationFailed";
        using var lifetime = new ClassicDesktopStyleApplicationLifetime();
        AppBuilder.Configure<Bydlo.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithLifetime(lifetime);
        var app = (Bydlo.App.App)Application.Current!;
        if (app.Services is not null || app.Desktop is not null || Directory.Exists(data))
        { throw new InvalidOperationException("Startup error initialized application services or data."); }
        var window = lifetime.MainWindow ?? throw new InvalidOperationException("Startup error has no window.");
        if (window.Content is not TextBlock { Text: { } text } || text != Loc.T("startup.activationFailed") ||
            Loc.Instance.Language != language || text == "startup.activationFailed")
        { throw new InvalidOperationException("Startup error was not localized."); }
        window.Show();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing startup error frame.");
        frame.Save(Path.Combine(output, "startup-error.png"));
        window.Close();
        Console.WriteLine("Startup error: visible localized window without services, recovery or data writes.");
    }
}
