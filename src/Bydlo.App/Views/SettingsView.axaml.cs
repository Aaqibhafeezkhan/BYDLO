using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Bydlo.App.Views;

public sealed partial class SettingsView : UserControl
{
    private bool _ready;
    private bool _updatingCategory;
    private int _jumpVersion;

    public SettingsView()
    {
        InitializeComponent();
        _ready = true;
        UpdateCategory("AppearanceSection");
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var sideIndex = e.NewSize.Width >= 660;
        SettingsLayout.ColumnDefinitions = new ColumnDefinitions(sideIndex ? "192,*" : "*,0");
        SettingsLayout.RowDefinitions = new RowDefinitions(sideIndex ? "*,0" : "Auto,*");
        Grid.SetColumn(SettingsContent, sideIndex ? 1 : 0);
        Grid.SetRow(SettingsContent, sideIndex ? 0 : 1);
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
        if (_ready && !_updatingCategory && sender is ComboBox { SelectedItem: ComboBoxItem { Tag: string section } })
        {
            JumpTo(section);
        }
    }

    private void UpdateCategory(string section)
    {
        _updatingCategory = true;
        foreach (var button in CategoryButtons.Children.OfType<Button>())
        {
            button.Classes.Set("active", Equals(button.Tag, section));
        }
        CompactCategory.SelectedItem = CompactCategory.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => Equals(item.Tag, section));
        _updatingCategory = false;
    }

    private void OnSettingsScrolled(object? sender, ScrollChangedEventArgs args)
    {
        if (!_ready) { return; }
        var section = CategoryButtons.Children.OfType<Button>()
            .Select(button => this.FindControl<Control>((string)button.Tag!))
            .LastOrDefault(control => control?.TranslatePoint(default, SettingsSections) is { } point &&
                point.Y <= SettingsScroll.Offset.Y + 1);
        if (section?.Name is { } name) { UpdateCategory(name); }
    }

    private void OnScrollSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (!_ready) { return; }
        // Leave enough trailing space to align even the final section at the top.
        ScrollTail.Height = Math.Max(0, SettingsScroll.Viewport.Height - AboutSection.Bounds.Height);
    }

    private void JumpTo(string section)
    {
        if (this.FindControl<Control>(section) is { } target)
        {
            UpdateCategory(section);
            var version = ++_jumpVersion;
            if ((target as Expander ?? target.GetVisualDescendants().OfType<Expander>()
                    .FirstOrDefault(expander => expander.Name == "AdvancedSettings")) is { } disclosure)
            {
                disclosure.IsExpanded = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (version != _jumpVersion) { return; }
                // BringIntoView only makes a section visible; it does not align its heading.
                // Keep focus on the category control so it cannot scroll an inner field into view.
                if (target.TranslatePoint(default, SettingsSections) is { } position)
                {
                    SettingsScroll.Offset = new Vector(0, position.Y);
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
