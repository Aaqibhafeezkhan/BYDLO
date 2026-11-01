using Avalonia;
using Avalonia.Controls;

namespace Bydlo.App.Views;

public sealed partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Use the content width: the desktop rail also takes space near its breakpoint.
        var stacked = e.NewSize.Width < 720;
        Dashboard.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "1.1*,*");
        Grid.SetColumn(ServerCard, stacked ? 0 : 1);
        Grid.SetRow(ServerCard, stacked ? 1 : 0);
        ServerCard.Margin = stacked ? new Thickness(0, 18, 0, 0) : new Thickness(18, 0, 0, 0);

        var contentWidth = Math.Max(0, e.NewSize.Width - 48);
        var columns = contentWidth >= 780 ? 3 : contentWidth >= 520 ? 2 : 1;
        QuickCards.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns)));
        Border[] cards = [TrafficCard, ZapretCard, TelegramCard];
        for (var i = 0; i < cards.Length; i++)
        {
            var column = i % columns;
            var row = i / columns;
            Grid.SetColumn(cards[i], column);
            Grid.SetRow(cards[i], row);
            cards[i].Margin = new Thickness(0, 0, column < columns - 1 ? 12 : 0, row < (cards.Length - 1) / columns ? 12 : 0);
        }

        EmptyCards.ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", columns)));
        Border[] emptyCards = [EmptyLinkCard, EmptyFileCard, EmptyQrCard];
        for (var i = 0; i < emptyCards.Length; i++)
        {
            var column = i % columns;
            var row = i / columns;
            Grid.SetColumn(emptyCards[i], column);
            Grid.SetRow(emptyCards[i], row);
            emptyCards[i].Margin = new Thickness(0, 0, column < columns - 1 ? 12 : 0, row < (emptyCards.Length - 1) / columns ? 12 : 0);
        }
    }
}
