using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;

internal static class LicenseVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        if (!shell.Services.IsMock) { throw new InvalidOperationException("Mock Mode required."); }
        foreach (var language in new[] { "ru", "en" })
        {
            Loc.Instance.SetLanguage(language);
            var view = new SettingsView { DataContext = shell.Settings };
            var window = new Window { Width = 1180, Height = 900, Content = view };
            window.Show();
            foreach (var width in new[] { 1180, 380 })
            {
                window.Width = width;
                Pump();
                var section = view.FindControl<Expander>("AboutSection")!;
                section.IsExpanded = true;
                Pump();
                var scroll = view.FindControl<ScrollViewer>("SettingsScroll")!;
                var content = view.FindControl<StackPanel>("SettingsSections")!;
                scroll.Offset = new Vector(0, section.TranslatePoint(default, content)!.Value.Y);
                Pump();
                var notices = view.FindControl<TextBlock>("LicenseNotices")!;
                if (notices.Text != Loc.Instance["about.licenses"] || !notices.IsEffectivelyVisible || notices.Bounds.Height <= 0 || notices.Bounds.Width > width)
                { throw new InvalidOperationException("License location must be visible and wrap within the window."); }
                foreach (var credit in SettingsViewModel.Credits)
                {
                    if (!window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == credit.Name))
                    { throw new InvalidOperationException("Missing credit: " + credit.Name); }
                }
                using var frame = window.CaptureRenderedFrame();
                frame!.Save(Path.Combine(output, $"licenses-{language}-{width}.png"));
            }
            window.Close();
        }
        Console.WriteLine("License UI verification passed: Russian/English, wide/narrow, notice locations and credits (Mock Mode).");
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }
}
