using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

internal static class ServerGroupVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        Wait(shell.Connections.ImportTextAsync("trojan://fake@one.invalid:443#Первый"));
        Wait(shell.Connections.ImportTextAsync("trojan://fake@two.invalid:443#Второй"));
        var window = new Window { Width = 1180, Height = 900, Content = new MainView { DataContext = shell } };
        window.Show(); Pump();
        var create = shell.Home.NewGroupCommand.ExecuteAsync(null);
        Pump();
        var editor = window.OwnedWindows.Single();
        var fields = editor.GetVisualDescendants().OfType<TextBox>().ToArray();
        fields[0].Text = "Группа Европа";
        var members = editor.GetVisualDescendants().OfType<CheckBox>().ToArray();
        Require(members.Length == 2, "Compatible profiles appear in the group editor");
        foreach (var member in members) { member.IsChecked = true; }
        var selectors = editor.GetVisualDescendants().OfType<ComboBox>().ToArray();
        selectors[2].SelectedIndex = (int)ServerGroupMode.Fallback;
        selectors[3].SelectedIndex = 1;
        using (var frame = editor.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "group-editor.png")); }
        var save = editor.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, Loc.Instance["common.save"]));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(create); Pump();
        var group = shell.Home.SelectedGroup!;
        Require(group is not null && group.ProfileIds.Count == 2 && shell.Home.UseGroup, "Group editor saves references and selects the group");
        Require(group!.Mode == ServerGroupMode.Fallback, "Failover mode is saved");
        Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null)); Pump();
        Require(shell.Primary.IsMock && shell.Home.IsGroupActive, "Group connects through MockEngine");
        Require(shell.Primary.SelectedGroupProfile?.Id == group.SelectedProfileId, "Home receives the selected member from the engine");
        Require(shell.Home.ActiveGroupServer.Contains("Группа Европа", StringComparison.Ordinal), "Home names the active group");
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "group-connected.png")); }
        window.Width = 380; window.Height = 700; Pump();
        using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, "group-connected-narrow.png")); }
        Wait(shell.Home.ToggleConnectionCommand.ExecuteAsync(null)); Pump();
        Require(!shell.Home.IsGroupActive, "Disconnect clears the active group");
        Wait(shell.Home.DeleteGroupCommand.ExecuteAsync(null));
        Require(!shell.Home.UseGroup && shell.Home.ServerGroups.Count == 0, "Group deletion clears selection");
        window.Close();
        Console.WriteLine("Server group verification passed: editor, persistence, mock connection, active member, deletion and narrow Home.");
    }

    private static void Wait(Task task)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
        Require(task.IsCompleted, "Operation completes"); task.GetAwaiter().GetResult();
    }
    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
