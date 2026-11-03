using Avalonia;
using Avalonia.Controls;

namespace Bydlo.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 620;
        ToolShortcut.IsVisible = compact;
        var stacked = e.NewSize.Width < 740;
        Landing.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,32,268");
        Grid.SetColumn(QuickTools, stacked ? 0 : 2);
        Grid.SetRow(QuickTools, stacked ? 1 : 0);
        QuickTools.Margin = new Thickness(0, stacked ? 24 : 0, 0, 0);
        StatusTitle.FontSize = compact ? 23 : 30;
    }
}
