using Avalonia;
using Avalonia.Controls;

namespace Bydlo.App.Views;

public sealed partial class ToolsView : UserControl
{
    public ToolsView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var compact = e.NewSize.Width < 640;
        foreach (var actions in new[] { ZapretActions, TgActions })
        {
            Grid.SetColumn(actions, compact ? 1 : 2);
            Grid.SetRow(actions, compact ? 1 : 0);
            actions.Margin = compact ? new Thickness(12, 12, 0, 0) : default;
        }
    }
}
