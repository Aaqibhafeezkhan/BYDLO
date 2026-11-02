using System.Collections.ObjectModel;
using Bydlo.App.Services;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ConnectionsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<SubscriptionRow> Subscriptions { get; } = [];

    [ObservableProperty] private bool _isWorking;

    private bool _autoRefreshStarted;

    public bool IsEmpty => Shell.Profiles.Count == 0;

    public override void OnShown()
    {
        Subscriptions.ReplaceWith(Services.Profiles.Subscriptions.Select(s => new SubscriptionRow(s)));
        RefreshCatalog();
        OnPropertyChanged(nameof(IsEmpty));
        _ = AutoRefreshIfDueAsync();
    }

    public Task AddImportedAsync(ImportResult result) => AddAsync(result, null);

    public async Task ImportExternalSubscriptionAsync(string url)
    {
        var preview = await Services.Subscriptions.FetchPreviewAsync(url);
        if (preview.Import.IsEmpty)
        {
            throw new InvalidOperationException(L["import.none"]);
        }

        var sub = await Services.Profiles.AddSubscriptionWithProfilesAsync(new Uri(url).Host, url, preview.Import.Profiles, preview.Metadata);
        Shell.ReloadProfiles();
        Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, preview.Import.Profiles.Count));
        OnShown();
    }

    [RelayCommand]
    private async Task RefreshSubscription(SubscriptionRow row)
    {
        IsWorking = true;
        try
        {
            await Shell.Guard(async () =>
            {
                var url = await Services.Profiles.GetSubscriptionUrlAsync(row.Id) ?? throw new InvalidOperationException("Subscription URL missing.");
                var preview = await Services.Subscriptions.FetchPreviewAsync(url, Services.ShutdownToken);
                if (preview.Import.IsEmpty)
                {
                    Shell.ShowToast((preview.Import.Errors.Count > 0 ? preview.Import.Errors[0] : null) ?? L["import.none"], isError: true);
                    return;
                }

                var selected = Shell.SelectedProfile;
                var selectedKey = selected is null ? null : ProfileKey(selected.Profile);
                await Services.Profiles.ReplaceSubscriptionProfilesAsync(row.Id, preview.Import.Profiles, preview.Metadata);
                Shell.ReloadProfiles();
                RestoreSelection(selectedKey);
                Shell.ShowToast(Loc.T("import.sub.updated", row.Snapshot.Name, preview.Import.Profiles.Count));
            });
        }
        finally
        {
            IsWorking = false;
            OnShown();
        }
    }

    [RelayCommand]
    private async Task RenameSubscription(SubscriptionRow row)
    {
        var name = (row.Name ?? "").Trim();
        if (name.Length == 0 || name == row.Snapshot.Name)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            await Services.Profiles.RenameSubscriptionAsync(row.Id, name);
            OnShown();
            Shell.ShowToast(L["toast.saved"]);
        });
    }

    [RelayCommand]
    private async Task DeleteSubscription(SubscriptionRow row)
    {
        if (Shell.Primary.Profile?.SubscriptionId == row.Id)
        {
            Shell.ShowToast(L["connections.delete.active"], isError: true);
            return;
        }
        await Shell.Guard(async () =>
        {
            await Services.Profiles.RemoveSubscriptionAsync(row.Id);
            Shell.ReloadProfiles();
            OnShown();
        });
    }

    /// <summary>Refreshes subscriptions whose data is older than the configured interval.</summary>
    public async Task AutoRefreshIfDueAsync()
    {
        if (_autoRefreshStarted || IsWorking)
        {
            return;
        }
        if (!Services.Settings.SubscriptionAutoUpdate || Services.IsMock || NetworkSafetyGuard.IsLocked)
        {
            return;
        }
        var intervalHours = Math.Clamp(Services.Settings.SubscriptionUpdateIntervalHours, 1, 168);
        var now = DateTimeOffset.UtcNow;
        var due = Services.Profiles.Subscriptions
            .Where(s => s.LastUpdated is null || now - s.LastUpdated > TimeSpan.FromHours(intervalHours))
            .ToList();
        if (due.Count == 0)
        {
            return;
        }
        _autoRefreshStarted = true;
        IsWorking = true;
        try
        {
            var selected = Shell.SelectedProfile;
            var selectedKey = selected is null ? null : ProfileKey(selected.Profile);
            var refreshed = 0;
            foreach (var sub in due)
            {
                try
                {
                    var url = await Services.Profiles.GetSubscriptionUrlAsync(sub.Id, Services.ShutdownToken);
                    if (url is null)
                    {
                        continue;
                    }
                    var preview = await Services.Subscriptions.FetchPreviewAsync(url, Services.ShutdownToken);
                    if (preview.Import.IsEmpty)
                    {
                        continue;
                    }
                    await Services.Profiles.ReplaceSubscriptionProfilesAsync(sub.Id, preview.Import.Profiles, preview.Metadata, Services.ShutdownToken);
                    refreshed++;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException)
                {
                    Services.Log.Warn("subs", $"Auto-update failed: {sub.Name}");
                }
            }
            if (refreshed > 0)
            {
                Shell.ReloadProfiles();
                RestoreSelection(selectedKey);
            }
        }
        finally
        {
            IsWorking = false;
            OnShown();
        }
    }

    private void RestoreSelection(string? key)
    {
        if (key is null)
        {
            return;
        }
        var match = Shell.Profiles.FirstOrDefault(p => ProfileKey(p.Profile) == key);
        if (match is not null)
        {
            Shell.SelectedProfile = match;
        }
    }

    private static string ProfileKey(Profile profile) =>
        $"{profile.Protocol}|{profile.Host}|{profile.Port}|{profile.Name}";

    [RelayCommand]
    private async Task Select(ProfileItem item)
    {
        Shell.SelectedProfile = item;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ConnectTo(ProfileItem item)
    {
        Shell.SelectedProfile = item;
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
