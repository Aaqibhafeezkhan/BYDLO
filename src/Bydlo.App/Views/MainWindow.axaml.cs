using Avalonia.Controls;
using Avalonia.Threading;
using Bydlo.App.ViewModels;
using Bydlo.Core.Import;

namespace Bydlo.App.Views;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        activationTimer.Tick += async (_, _) =>
        {
            if (DataContext is MainViewModel { CanReceiveActivation: true } vm &&
                App.PendingActivations.TryDequeue(out var inputs))
            {
                Show();
                WindowState = WindowState.Normal;
                Activate();
                if (inputs.Length > 0)
                {
                    await vm.PreviewExternalAsync(inputs);
                }
            }
        };
        Opened += (_, _) => activationTimer.Start();
        Closed += (_, _) => activationTimer.Stop();
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
