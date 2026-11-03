using Avalonia;
using Avalonia.Media;

namespace Bydlo.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Do this before claiming the single-instance mutex or forwarding activation.
        // Otherwise the elevated child forwards back to the parent and exits during UAC restart.
        if (OperatingSystem.IsWindows() && !ElevationHandoff.WaitForParent(ref args))
        {
            Environment.ExitCode = 1;
            return;
        }

        var dataDirectory = Bydlo.Platform.AppPathsFactory.Create().DataDirectory;
        using var activation = OperatingSystem.IsWindows() ? new WindowsActivationBroker(
            dataDirectory, Bydlo.App.App.PendingActivations.Enqueue) : null;
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

        // Keep the lock file in place. Deleting it would let another process lock a different inode.
        FileStream? dataLock = null;
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                dataLock = new FileStream(Path.Combine(dataDirectory, "desktop.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine("BYDLO could not lock its data directory: " + ex.Message);
                Environment.ExitCode = 1;
                return;
            }
        }

        using var exclusiveDataAccess = dataLock;
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
