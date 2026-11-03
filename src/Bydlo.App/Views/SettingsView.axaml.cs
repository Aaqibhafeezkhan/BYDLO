using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bydlo.App.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var sideIndex = e.NewSize.Width >= 660;
        SettingsLayout.ColumnDefinitions = new ColumnDefinitions(sideIndex ? "192,*" : "*,0");
        SettingsLayout.RowDefinitions = new RowDefinitions(sideIndex ? "*,0" : "Auto,*");
        Grid.SetColumn(SettingsScroll, sideIndex ? 1 : 0);
        Grid.SetRow(SettingsScroll, sideIndex ? 0 : 1);
        CategoryScroll.IsVisible = sideIndex;
        CompactCategory.IsVisible = !sideIndex;
        CategoryIndex.BorderThickness = sideIndex ? new Thickness(0, 0, 1, 0) : new Thickness(0, 0, 0, 1);
        CategoryIndex.Padding = sideIndex ? new Thickness(0, 0, 10, 0) : new Thickness(0, 0, 0, 8);
        CategoryIndex.Margin = sideIndex ? new Thickness(0, 0, 12, 0) : new Thickness(0, 0, 0, 12);
    }

    private void ScrollToSection(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string section })
        {
            JumpTo(section);
        }
    }

    private void OnCategorySelected(object? sender, SelectionChangedEventArgs args)
    {
        if (sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string section } } picker)
        {
            JumpTo(section);
            picker.SelectedIndex = -1;
        }
    }

    private void JumpTo(string section)
    {
        if (this.FindControl<Control>(section) is { } target)
        {
            if ((target as Expander ?? target.GetVisualDescendants().OfType<Expander>()
                    .FirstOrDefault(expander => expander.Name == "AdvancedSettings")) is { } disclosure)
            {
                disclosure.IsExpanded = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                target.BringIntoView(new Rect(0, 0, target.Bounds.Width, 48));
                target.GetVisualDescendants().OfType<Control>()
                    .FirstOrDefault(control => control.Focusable && control.IsEffectivelyVisible && control.IsEffectivelyEnabled)
                    ?.Focus();
            }, DispatcherPriority.Loaded);
        }
    }
}
