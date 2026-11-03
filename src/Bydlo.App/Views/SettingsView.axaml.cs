using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Bydlo.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void ScrollToSection(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string section } && this.FindControl<Control>(section) is { } target)
        {
            target.BringIntoView();
        }
    }
}
