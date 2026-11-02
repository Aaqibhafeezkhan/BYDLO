using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;

internal static class ShareVerification
{
    // Fake credentials and an isolated headless clipboard; no engines or system networking.
    public static void Run(MainViewModel shell, string output)
    {
        shell.FinishOnboardingCommand.Execute(null);
        Wait(shell.Connections.ImportTextAsync("trojan://fake%3Apassword@example.invalid:443?sni=example.invalid#QR%20Тест"));
        shell.Navigate("connections");
        var view = new MainView { DataContext = shell };
        var window = new Window { Width = 1180, Height = 900, Content = view };
        window.Show();
        Pump();
        var menuButton = view.GetVisualDescendants().OfType<Button>().First(b => Equals(b.Content, "⋯"));
        var flyout = (Flyout)menuButton.Flyout!;
        flyout.ShowAt(menuButton);
        Pump();
        var actions = ((StackPanel)flyout.Content!).Children.OfType<Button>().ToArray();
        Require(actions[0].Command is not null && actions[1].Command is not null, "Share menu commands resolve in flyout");
        Require(actions[0].IsVisible && actions[1].IsVisible && !actions[2].IsVisible, "Protocol-specific actions");
        using (var frame = window.CaptureRenderedFrame()) { frame?.Save(Path.Combine(output, "share-menu.png")); }
        flyout.Hide();

        var item = shell.Profiles.Last();
        var task = shell.Connections.ShowQrCommand.ExecuteAsync(item);
        Pump();
        var dialog = window.OwnedWindows.Single();
        var panel = (StackPanel)dialog.Content!;
        Require(!panel.Children.OfType<Image>().Any(), "No QR before warning confirmation");
        using (var frame = dialog.CaptureRenderedFrame()) { frame?.Save(Path.Combine(output, "share-warning.png")); }
        var confirm = panel.Children.OfType<Button>().First();
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        var image = panel.Children.OfType<Image>().Single();
        var decoded = QrDecoder.Decode((Avalonia.Media.Imaging.Bitmap)image.Source!);
        Require(decoded is not null, "Generated bitmap is readable by QR importer");
        var imported = shell.Services.Importer.ImportText(decoded!);
        Require(imported.Profiles.Count == 1 && imported.Profiles[0].Secrets["password"] == "fake:password", "QR preserves credentials");
        using (var frame = dialog.CaptureRenderedFrame())
        {
            Require(frame is not null && QrDecoder.Decode(frame) == decoded, "Displayed QR remains readable at its rendered size");
            frame!.Save(Path.Combine(output, "share-qr.png"));
        }
        dialog.Close();
        Wait(task);

        var copy = shell.Connections.CopyLinkCommand.ExecuteAsync(item);
        Pump();
        dialog = window.OwnedWindows.Single();
        ((StackPanel)dialog.Content!).Children.OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Wait(copy);
        Require(ReadClipboard(window) == decoded, "Clipboard receives the same share link");

        var cancelled = shell.Connections.CopyLinkCommand.ExecuteAsync(item);
        Pump();
        window.OwnedWindows.Single().Close();
        Wait(cancelled);
        Require(ReadClipboard(window) == decoded, "Cancelling warning leaves clipboard intact");

        Wait(shell.Connections.ImportTextAsync("[Interface]\nPrivateKey = fake\nAddress = 10.0.0.2/32\n[Peer]\nPublicKey = fake-public\nEndpoint = example.invalid:51820"));
        Pump();
        Require(shell.Profiles.Last().CanExportConfig && !shell.Profiles.Last().CanShareLink, "WireGuard uses config export instead of share links");
        window.Close();
        Console.WriteLine("Share verification passed: menu bindings, warning confirmation, QR decode, clipboard.");
    }

    private static string? ReadClipboard(Window window)
    {
        var read = Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!);
        Wait(read);
        return read.Result;
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
