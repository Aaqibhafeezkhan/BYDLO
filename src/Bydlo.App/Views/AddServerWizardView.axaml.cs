using Avalonia.Controls;
using Bydlo.App.ViewModels;

namespace Bydlo.App.Views;

public sealed partial class AddServerWizardView : UserControl
{
    public AddServerWizardView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null) { _viewModel.PropertyChanged -= OnStepChanged; }
        _viewModel = DataContext as AddServerWizardViewModel;
        if (_viewModel is not null) { _viewModel.PropertyChanged += OnStepChanged; }
    }

    private AddServerWizardViewModel? _viewModel;
    private void OnStepChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddServerWizardViewModel.Step)) { WizardScroll.Offset = default; }
    }
}
