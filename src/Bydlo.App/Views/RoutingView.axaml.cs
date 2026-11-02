using Avalonia;
using Avalonia.Controls;

namespace Bydlo.App.Views;

public sealed partial class RoutingView : UserControl
{
    public RoutingView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 640;
        Grid.SetColumn(SaveButton, compact ? 0 : 1);
        Grid.SetRow(SaveButton, compact ? 1 : 0);
        SaveButton.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;
        SaveButton.Margin = compact ? new Thickness(0, 16, 0, 0) : default;
    }
}
