using Avalonia;
using Avalonia.Controls;
using Bydlo.App.ViewModels;

namespace Bydlo.App.Views;

public sealed partial class MainView : UserControl
{
    /// <summary>Below this width the sidebar collapses into bottom navigation (phones, narrow windows).</summary>
    public const double CompactWidth = 760;

    public MainView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is MainViewModel vm)
        {
            vm.Services.TopLevel = () => TopLevel.GetTopLevel(this);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (DataContext is MainViewModel vm)
        {
            vm.IsCompact = e.NewSize.Width < CompactWidth;
        }
    }
}
