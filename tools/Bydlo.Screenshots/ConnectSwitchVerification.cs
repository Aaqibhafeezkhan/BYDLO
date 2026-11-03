using Avalonia.Threading;
using Bydlo.App.ViewModels;

internal static class ConnectSwitchVerification
{
    // Two mock profiles: the power button must move the live session (issue #141). No engines or network.
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        Wait(shell.Connections.ImportTextAsync("trojan://pw@one.example.invalid:443#One"));
        Wait(shell.Connections.ImportTextAsync("trojan://pw@two.example.invalid:443#Two"));
        Require(shell.Profiles.Count == 2, "Two mock profiles are listed");
        var first = shell.Profiles[0];
        var second = shell.Profiles[1];

        Wait(shell.Connections.ConnectToCommand.ExecuteAsync(first));
        Pump();
        Require(shell.Home.IsConnected, "First server connects from the list");
        Require(shell.Primary.Profile?.Id == first.Id, "Active session is the first server");

        Wait(shell.Connections.ConnectToCommand.ExecuteAsync(second));
        Pump();
        Require(shell.Home.IsConnected, "Session stays connected after switching servers");
        Require(shell.Primary.Profile?.Id == second.Id, "Active session switches to the second server");

        Wait(shell.Connections.ConnectToCommand.ExecuteAsync(second));
        Pump();
        Require(shell.Home.IsConnected, "Reordering the active server keeps the session");
        Require(shell.Primary.Profile?.Id == second.Id, "Active session is still the second server");

        Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
        Pump();
        Require(!shell.Home.IsConnected, "Cleanup disconnects");
        Console.WriteLine("Connect-switch verification passed: the list power button moves the live session (issue #141).");
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
