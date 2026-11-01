using Avalonia;
using Avalonia.Media;

namespace Bydlo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var activation = OperatingSystem.IsWindows() ? new WindowsActivationBroker(Bydlo.Platform.AppPathsFactory.Create().DataDirectory) : null;
        if (activation is { IsPrimary: false })
        {
            try
            {
                activation.Forward(args);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
            {
                Environment.ExitCode = 1;
            }

            return;
        }

        activation?.Listen();
        Bydlo.App.App.Args = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Bydlo.App.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            .LogToTrace();
}
