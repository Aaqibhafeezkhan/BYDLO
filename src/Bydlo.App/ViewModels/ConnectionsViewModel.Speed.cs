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

    public string? SpeedTestDisabledReason => !MeasuringSpeed && (Services.IsMock || NetworkSafetyGuard.IsLocked)
        ? L["connections.speed.unavailable"] : null;

    partial void OnMeasuringSpeedChanged(bool value) => OnPropertyChanged(nameof(SpeedTestDisabledReason));

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
        catch (Bydlo.Core.Engines.EngineException ex) when (ex.LocalizationKey == "error.engine.cleanup")
        {
            Shell.ShowToast(L["error.engine.cleanup"], isError: true);
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
