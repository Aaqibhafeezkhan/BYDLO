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
            Grid.SetColumn(actions, compact ? 0 : 2);
            Grid.SetColumnSpan(actions, compact ? 3 : 1);
            Grid.SetRow(actions, compact ? 1 : 0);
            actions.Margin = compact ? new Thickness(0, 12, 0, 0) : default;
        }
    }
}
