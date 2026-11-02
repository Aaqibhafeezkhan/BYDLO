using Bydlo.Core.Engines;
using Bydlo.Core.Security;
using Bydlo.Core.Warp;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>In-app Cloudflare WARP registration (issue #85). Explicit user action only.</summary>
public sealed partial class ConnectionsViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRegisterWarp))]
    private bool _isWarpBusy;

    public bool CanRegisterWarp => !IsWorking && !IsWarpBusy;

    [RelayCommand]
    private async Task RegisterWarp()
    {
        if (IsWorking || IsWarpBusy)
        {
            return;
        }

        IsWarpBusy = true;
        try
        {
            await Shell.Guard(async () =>
            {
                if (Services.IsMock || NetworkSafetyGuard.IsLocked)
                {
                    throw new EngineException("Real WARP registration is disabled.", localizationKey: "warp.locked");
                }

                var client = new WarpClient(Services.Http, Services.Log.Redactor);
                // No ConfigureAwait(false): the lines below touch bound collections and must resume on the UI thread.
                var registration = await client.RegisterAsync(options: null, Services.ShutdownToken);
                var imported = WarpProfileFactory.Create(L["warp.title"], registration);
                var added = await Services.Profiles.AddAsync([imported], ct: Services.ShutdownToken);
                Shell.ReloadProfiles();
                Shell.SelectedProfile = Shell.Profiles.FirstOrDefault(p => p.Id == added[0].Id) ?? Shell.SelectedProfile;
                Shell.SelectedChain = null;
                Shell.ShowToast(L["warp.added"]);
            });
        }
        finally
        {
            IsWarpBusy = false;
        }
    }
}
