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
        StatusTitle.FontSize = compact ? 30 : 38;
        Hero.Margin = new Thickness(0, compact ? 20 : 28, 0, 0);
    }
}
