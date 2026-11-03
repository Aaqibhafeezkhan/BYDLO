using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bydlo.App.Services;
using Bydlo.App.ViewModels;
using Bydlo.App.Views;
using Bydlo.Core.Models;

internal static class DisclosureVerification
{
    public static void Run(MainViewModel shell, string output)
    {
        var window = new Window { Width = 1180, Height = 900, Content = new MainView { DataContext = shell } };
        window.Show();
        Require(shell.IsOnboardingVisible, "Fresh data shows onboarding");
        foreach (var theme in new[] { ThemePreference.Light, ThemePreference.Oled })
        foreach (var width in new[] { 1180, 620 })
        {
            shell.Services.Theme.Apply(theme);
            window.Width = width;
            Pump();
            var expander = window.GetVisualDescendants().OfType<Expander>()
                .Single(e => Equals(e.Header, Loc.Instance["onboarding.appearance"]));
            var toggle = expander.GetVisualDescendants().OfType<ToggleButton>().First();
            foreach (var fraction in new[] { 0.05, 0.5, 0.95 })
            {
                var point = toggle.TranslatePoint(new Point(toggle.Bounds.Width * fraction, toggle.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();
                Require(expander.IsExpanded, $"Disclosure opens at {fraction:P0} of the header width");
                point = toggle.TranslatePoint(new Point(toggle.Bounds.Width * fraction, toggle.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();
                Require(!expander.IsExpanded, "The same point closes the disclosure");
            }
            toggle.Focus();
            window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
            Pump();
            Require(expander.IsExpanded, "Keyboard activation still opens the disclosure");
            using (var frame = window.CaptureRenderedFrame()) { frame!.Save(Path.Combine(output, $"disclosure-{theme}-{width}.png")); }
            expander.IsExpanded = false;
        }
        window.Close();
        Require(shell.Services.IsMock, "Verification stays in Mock Mode");
        Console.WriteLine("Disclosure verification passed: left, center, right, keyboard, Light/Black, wide/narrow.");
    }

    private static void Pump()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Dispatcher.UIThread.MainLoop(timeout.Token);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
