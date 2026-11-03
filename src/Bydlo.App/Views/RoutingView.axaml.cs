using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Bydlo.App.ViewModels;
using Bydlo.Core.Routing;

namespace Bydlo.App.Views;

public sealed partial class RoutingView : UserControl
{
    internal static readonly DataFormat<string> RuleDragFormat = DataFormat.CreateStringApplicationFormat("bydlo-routing-rule");
    private string? _dragToken;
    private OrderedRoutingRule? _draggedRule;

    public RoutingView()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(RuleList, true);
        RuleList.AddHandler(DragDrop.DragOverEvent, OnRuleDragOver);
        RuleList.AddHandler(DragDrop.DropEvent, OnRuleDrop);
    }

    private async void OnRuleDragStart(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OrderedRoutingRule rule } handle ||
            !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed || _dragToken is not null)
        {
            return;
        }

        _dragToken = Guid.NewGuid().ToString("N");
        _draggedRule = rule;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(RuleDragFormat, _dragToken));
        e.Handled = true;
        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragToken = null;
            _draggedRule = null;
        }
    }

    private void OnRuleDragOver(object? sender, DragEventArgs e)
    {
        if (_dragToken is not null && e.DataTransfer.TryGetValue(RuleDragFormat) == _dragToken)
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnRuleDrop(object? sender, DragEventArgs e)
    {
        if (_dragToken is null || e.DataTransfer.TryGetValue(RuleDragFormat) != _dragToken ||
            _draggedRule is not { } source || DataContext is not RoutingViewModel vm || e.Source is not Visual visual)
        {
            return;
        }

        var target = visual as ListBoxItem ?? visual.FindAncestorOfType<ListBoxItem>();
        if (target?.DataContext is OrderedRoutingRule destination)
        {
            vm.MoveRuleTo(source, destination);
            e.Handled = true;
        }
    }

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
