using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

internal static class OverhaulVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        Require(shell.Services.IsMock, "Visual verification must run in Mock Mode.");
        if (shell.IsOnboardingVisible)
        {
            foreach (var width in new[] { 1180, 380 })
            {
                Capture(shell, output, $"onboarding-{width}", width);
            }
            shell.FinishOnboardingCommand.Execute(null);
        }

        Capture(shell, output, "home-empty-380", 380);
        Wait(shell.Connections.AddDemoProfilesCommand.ExecuteAsync(null));
        shell.Toast = null;
        Require(shell.Profiles.Count > 0, "Demo profiles are available.");

        var search = shell.Profiles[0].Endpoint;
        shell.Connections.ProfileSearch = search;
        Require(shell.Connections.VisibleProfiles.Any() &&
                shell.Connections.VisibleProfiles.All(profile => profile.Endpoint.Contains(search, StringComparison.OrdinalIgnoreCase)),
            "Search filters the current server list.");
        shell.Connections.ProfileSearch = "no-match-for-visual-verification";
        Require(shell.Connections.HasNoMatchingProfiles, "An unmatched query gets an empty state.");
        shell.Connections.ProfileSearch = null;

        shell.Navigate("connections");
        Capture(shell, output, "connections-compact-actions", 380, window =>
        {
            var profileCount = shell.Profiles.Count;
            var delete = window.GetVisualDescendants().OfType<Button>().First(button =>
                AutomationProperties.GetName(button) == Loc.T("connections.delete"));
            Require(delete.Command is null && delete.Flyout is Flyout, "Opening delete cannot delete a server directly.");
            var flyout = (Flyout)delete.Flyout!;
            flyout.ShowAt(delete);
            Pump();
            var confirm = ((StackPanel)flyout.Content!).Children.OfType<Button>().Single();
            Require(ReferenceEquals(confirm.Command, shell.Connections.DeleteCommand) && confirm.CommandParameter is ProfileItem,
                "The confirmation resolves the delete command and server.");
            flyout.Hide();
            Require(shell.Profiles.Count == profileCount, "Dismissing confirmation keeps the server.");
        });

        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        {
            shell.Settings.Theme = theme;
            foreach (var width in new[] { 1180, 380 })
            {
                foreach (var page in new[] { "home", "connections", "tools", "settings", "routing", "statistics" })
                {
                    shell.Navigate(page);
                    Capture(shell, output, $"{page}-{theme}-{width}", width);
                }

                shell.OpenAddServerCommand.Execute(null);
                Require(!shell.CanUseWorkspace, "The import overlay blocks keyboard access to the background.");
                Capture(shell, output, $"import-{theme}-{width}", width);
                shell.AddServerWizard.CloseCommand.Execute(null);

                shell.Navigate("connections");
                shell.NewChainCommand.Execute(null);
                Capture(shell, output, $"chain-{theme}-{width}", width);
                shell.ChainEditor.CloseCommand.Execute(null);
            }
        }

        shell.Navigate("home");
        Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));
        Pump();
        Require(shell.Home.IsConnected, "The primary action connects through MockEngine.");
        Capture(shell, output, "home-connected-380", 380);
        Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null));

        shell.Settings.ReduceMotion = false;
        Capture(shell, output, "motion-standard", 380, window =>
        {
            Require(PageHost(window).PageTransition is not null, "Standard mode uses a page transition.");
            shell.Settings.ReduceMotion = true;
            Pump();
            Require(PageHost(window).PageTransition is null, "Reduced motion removes the page transition immediately.");
            var expanders = window.GetVisualDescendants().OfType<Expander>();
            Require(expanders.Any() && expanders.All(control => control.ContentTransition is null),
                "Reduced motion removes disclosure transitions.");
        });
        Console.WriteLine("Overhaul verification passed: layout matrix, server search, mock connection, reduced motion.");
    }

    private static TransitioningContentControl PageHost(Window window) => window.GetVisualDescendants()
        .OfType<TransitioningContentControl>().Single(control => control.Classes.Contains("page-host"));

    private static void Capture(MainViewModel shell, string output, string name, int width, Action<Window>? verify = null)
    {
        var window = new Window { Width = width, Height = 880, Content = new MainView { DataContext = shell } };
        window.Show();
        Pump();
        Require(shell.IsCompact == (width < MainView.CompactWidth), "The layout follows the shell breakpoint.");
        verify?.Invoke(window);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame: " + name);
        frame.Save(Path.Combine(output, name + ".png"));
        window.Close();
    }

    private static void Wait(Task task)
    {
        while (!task.IsCompleted) { Pump(); }
        task.GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
