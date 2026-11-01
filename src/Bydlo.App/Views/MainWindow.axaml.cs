using Avalonia.Controls;
using Bydlo.App.ViewModels;
using Bydlo.Core.Import;

namespace Bydlo.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                var inputs = ExternalImportInput.LaunchInputs(App.Args).ToArray();
                if (inputs.Length > 0)
                {
                    await vm.PreviewExternalAsync(inputs);
                }

                await vm.CheckClipboardAsync();
            }
        };
        Activated += async (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                await vm.CheckClipboardAsync();
            }
        };
    }
}
