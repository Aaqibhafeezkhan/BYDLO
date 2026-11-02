using System.Collections.ObjectModel;
using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ConnectionsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<Subscription> Subscriptions { get; } = [];

    [ObservableProperty] private bool _isWorking;

    [ObservableProperty] private bool _isCheckingLatency;

    /// <summary>User-initiated latency probe only; never runs in Mock Mode or under the safety lock.</summary>
    public bool CanCheckLatency => !IsCheckingLatency && !Services.IsMock && !NetworkSafetyGuard.IsLocked;

    public string? CheckLatencyDisabledReason => IsCheckingLatency ? null
        : Services.IsMock ? L["connections.latency.mock"]
        : NetworkSafetyGuard.IsLocked ? L["connections.latency.locked"] : null;

    partial void OnIsCheckingLatencyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCheckLatency));
        OnPropertyChanged(nameof(CheckLatencyDisabledReason));
    }

    [RelayCommand]
    private async Task CheckLatency()
    {
        if (IsCheckingLatency || Services.IsMock || NetworkSafetyGuard.IsLocked)
        {
            return;
        }

        IsCheckingLatency = true;
        try
        {
            IReadOnlyDictionary<Guid, ServerLatencyResult>? results = null;
            await Shell.Guard(async () =>
            {
                results = await Services.ServerLatency.ProbeAsync(Services.Profiles.Profiles).ConfigureAwait(false);
            });
            if (results is not null)
            {
                Shell.SetLatencies(results);
            }
        }
        finally
        {
            IsCheckingLatency = false;
        }
    }

    public bool IsEmpty => Shell.Profiles.Count == 0;

    public override void OnShown()
    {
        Subscriptions.ReplaceWith(Services.Profiles.Subscriptions);
        RefreshCatalog();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanCheckLatency));
        OnPropertyChanged(nameof(CheckLatencyDisabledReason));
    }

    public Task AddImportedAsync(ImportResult result) => AddAsync(result, null);

    public async Task ImportExternalSubscriptionAsync(string url)
    {
        var result = await Services.Subscriptions.FetchAsync(url);
        if (result.IsEmpty)
        {
            throw new InvalidOperationException(L["import.none"]);
        }

        var sub = await Services.Profiles.AddSubscriptionWithProfilesAsync(new Uri(url).Host, url, result.Profiles);
        Shell.ReloadProfiles();
        Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, result.Profiles.Count));
        OnShown();
    }

    [RelayCommand]
    private async Task RefreshSubscription(Subscription sub)
    {
        IsWorking = true;
        try
        {
            await Shell.Guard(async () =>
            {
                var url = await Services.Profiles.GetSubscriptionUrlAsync(sub.Id) ?? throw new InvalidOperationException("Subscription URL missing.");
                var result = await Services.Subscriptions.FetchAsync(url);
                if (result.IsEmpty)
                {
                    Shell.ShowToast((result.Errors.Count > 0 ? result.Errors[0] : null) ?? L["import.none"], isError: true);
                    return;
                }

                await Services.Profiles.ReplaceSubscriptionProfilesAsync(sub.Id, result.Profiles);
                Shell.ReloadProfiles();
                Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, result.Profiles.Count));
            });
        }
        finally
        {
            IsWorking = false;
            OnShown();
        }
    }

    [RelayCommand]
    private async Task Select(ProfileItem item)
    {
        Shell.SelectedProfile = item;
        Shell.SelectedChain = null;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConnectTo(ProfileItem item)
    {
        Shell.SelectedProfile = item;
        Shell.SelectedChain = null;
        Shell.Navigate("home");
        if (!Shell.Home.IsConnected)
        {
            await Shell.Home.ToggleConnectionCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task ToggleFavorite(ProfileItem item)
    {
        item.Profile.IsFavorite = !item.Profile.IsFavorite;
        await Services.Profiles.UpdateAsync(item.Profile);
        Shell.ReloadProfiles();
    }

    [RelayCommand]
    private Task CopyLink(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.copy");

    [RelayCommand]
    private Task ShowQr(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.qr");

    [RelayCommand]
    private Task ExportConfig(ProfileItem item) => ProfileShareDialog.ShowAsync(Services, item.Profile, "share.file");

    [RelayCommand]
    private async Task Delete(ProfileItem item)
    {
        if (Shell.Primary.Profile?.Id == item.Id)
        {
            Shell.ShowToast(L["connections.delete.active"], isError: true);
            return;
        }

        if (Shell.Primary.Chain?.ProfileIds.Contains(item.Id) == true)
        {
            Shell.ShowToast(L["chain.delete.active"], isError: true);
            return;
        }

        await Services.Profiles.RemoveAsync(item.Id);
        Shell.ReloadProfiles();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task AddDemoProfiles()
    {
        // Mock Mode helper: fake, clearly labelled profiles for UI testing. They never reach a real engine.
        var demo = new ImportResult(
        [
            Demo("Demo · Amsterdam", ProtocolKind.Vless, "nl.demo.invalid"),
            Demo("Demo · Frankfurt", ProtocolKind.Hysteria2, "de.demo.invalid"),
            Demo("Demo · Helsinki", ProtocolKind.WireGuard, "fi.demo.invalid"),
        ], []);
        await AddAsync(demo, null);

        static ImportedProfile Demo(string name, ProtocolKind kind, string host) =>
            new(new Profile { Name = name, Protocol = kind, Host = host, Port = 443, Options = { ["demo"] = "true" } }, new ProfileSecrets());
    }

    /// <summary>Imports supplied text for internal callers; interactive entry points use the wizard.</summary>
    public Task ImportTextAsync(string text) => ImportAsync(text, null);

    private async Task ImportAsync(string text, Guid? subscription)
    {
        if (ProfileImporter.LooksLikeSubscriptionUrl(text))
        {
            await ImportExternalSubscriptionAsync(text.Trim());
            return;
        }
        await AddAsync(Services.Importer.ImportText(text), subscription);
    }

    private async Task AddAsync(ImportResult result, Guid? subscription)
    {
        if (result.Profiles.Count > 0)
        {
            var added = await Services.Profiles.AddAsync(result.Profiles, subscription);
            Shell.ReloadProfiles();
            Shell.SelectedProfile = Shell.Profiles.FirstOrDefault(p => p.Id == added[0].Id) ?? Shell.SelectedProfile;

            Shell.ShowToast(Loc.T("import.added", added.Count) + (result.Errors.Count > 0 ? " " + Loc.T("import.skipped", result.Errors.Count) : ""));
        }
        else
        {
            Shell.ShowToast((result.Errors.Count > 0 ? result.Errors[0] : null) ?? L["import.none"], isError: true);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
