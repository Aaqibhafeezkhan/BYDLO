using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ConnectionsViewModel
{
    [ObservableProperty] private string _speedTestUrl = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanMeasureSpeed))]
    private bool _measuringSpeed;

    public bool CanMeasureSpeed => !MeasuringSpeed && !Services.IsMock && !NetworkSafetyGuard.IsLocked;

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task MeasureSpeed(CancellationToken cancellationToken)
    {
        if (!CanMeasureSpeed)
        {
            return;
        }
        if (!Uri.TryCreate(SpeedTestUrl.Trim(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            Shell.ShowToast(L["connections.speed.invalid"], isError: true);
            return;
        }

        MeasuringSpeed = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Services.ShutdownToken);
        try
        {
            var results = await Services.Connections.MeasureServerSpeedsAsync(
                Services.Profiles.Profiles.ToArray(), url, Services.EffectiveSettings, linked.Token);
            Shell.SetServerSpeeds(results);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Bydlo.Core.Engines.EngineException)
        {
            Shell.ShowToast(L["connections.speed.failed"], isError: true);
        }
        finally
        {
            MeasuringSpeed = false;
        }
    }
}
