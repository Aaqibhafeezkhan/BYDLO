using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Security;

internal static class BundledEnginesVerification
{
    public static void Run(MainViewModel shell, AppServices services, string output)
    {
        if (!services.IsMock || !NetworkSafetyGuard.IsLocked) { throw new InvalidOperationException("Mock and safety lock required."); }
        shell.Tools.OnShown();
        var window = new Window { Width = 1000, Height = 900, Content = new ToolsView { DataContext = shell.Tools } };
        window.Show();
        Pump(200);
        var section = window.GetVisualDescendants().OfType<Expander>().Single(e => e.Header?.ToString() == Loc.Instance["tools.engines"]);
        section.IsExpanded = true;
        Pump(200);
        if (services.Locator.Platform.Os == Bydlo.Core.Models.OsFamily.Windows)
        {
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == Loc.Instance["tools.openvpn.driver.install"]);
            if (button.IsEnabled) { throw new InvalidOperationException("Driver setup must require explicit consent."); }
            shell.Tools.OpenVpnDriverConsent = true;
            Pump(30);
            if (!button.IsEnabled) { throw new InvalidOperationException("Consent did not enable driver setup."); }
        }
        shell.Tools.OpenVpnDriverConsent = true;
        var setup = shell.Tools.InstallOpenVpnDriverCommand.ExecuteAsync(null);
        while (!setup.IsCompleted) { Pump(10); }
        setup.GetAwaiter().GetResult();
        if (shell.Toast != Loc.Instance["tools.openvpn.driver.mock"]) { throw new InvalidOperationException("Driver setup was not blocked in mock mode."); }
        shell.Tools.OpenVpnDriverConsent = false;
        foreach (var (name, width) in new[] { ("bundled-engines", 1000), ("bundled-engines-narrow", 400) })
        {
            window.Width = width;
            Pump(100);
            var scroll = section.GetVisualAncestors().OfType<ScrollViewer>().First();
            var position = section.TranslatePoint(new Point(), (Visual)scroll.Content!);
            scroll.Offset = new Vector(0, position!.Value.Y - 16);
            Pump(100);
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing bundle UI frame.");
            frame.Save(Path.Combine(output, name + ".png"));
        }
        window.Close();
        Console.WriteLine("Bundled engine UI: consent, mock/safety refusal and wide/narrow rendering passed.");
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); } while (DateTime.UtcNow < until);
    }
}
