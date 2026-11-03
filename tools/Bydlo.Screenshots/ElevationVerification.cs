using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Connection;
using Bydlo.Core.Platform;

internal static class ElevationVerification
{
    // No UAC or real engines: exercise the actual command with a declined fake privilege service.
    public static void Run(string output)
    {
        var privileges = new DeclinedPrivileges();
        var services = Wait(AppServices.CreateAsync(["--mock"], privileges));
        var shell = new MainViewModel(services);
        shell.FinishOnboardingCommand.Execute(null);
        Wait(shell.Connections.ImportTextAsync("trojan://pw@elevation.example.invalid:443#Elevation"));
        Wait(shell.Connections.ConnectToCommand.ExecuteAsync(shell.Profiles[0]));
        var window = new MainWindow { DataContext = shell, Width = 1180, Height = 780 };
        window.Show();
        Wait(shell.Settings.RestartElevatedCommand.ExecuteAsync(null));
        Require(privileges.Requested, "The actual command requests elevation");
        Require(services.Connections.Primary.Status == ConnectionStatus.Connected, "Cancelled UAC preserves the mock session");
        Require(shell.Toast == Loc.Instance["settings.admin.cancelled"], "Cancellation is visible");
        shell.Toast = null;
        shell.Home.IsTun = true;
        Capture("elevation-home", 1180, 780);
        Capture("elevation-home-compact", 400, 760);
        shell.NavigateCommand.Execute("settings");
        Capture("elevation-settings", 1180, 1000);
        Wait(services.Connections.StopAllAsync());
        window.Close();
        Wait(services.ShutdownAsync());
        Console.WriteLine("Elevation verification passed: actual command preserves active mock connection on cancelled UAC, shows notification, and exposes restart beside TUN and connection settings.");

        void Capture(string name, int width, int height)
        {
            window.Width = width;
            window.Height = height;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            Dispatcher.UIThread.MainLoop(timeout.Token);
            var restart = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b =>
                ReferenceEquals(b.Command, shell.Settings.RestartElevatedCommand) && b.IsEffectivelyVisible);
            Require(restart is not null, "Restart button is visible near the feature");
            restart!.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame");
            frame.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class DeclinedPrivileges : IPrivilegeService
    {
        public bool IsElevated => false;
        public bool CanRestartElevated => true;
        public bool Requested { get; private set; }
        public bool TryRestartElevated(IReadOnlyList<string> args) { Requested = true; return false; }
    }
}
