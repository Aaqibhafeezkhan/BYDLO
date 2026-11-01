using Avalonia;
using Avalonia.Media;

namespace Bydlo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
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
