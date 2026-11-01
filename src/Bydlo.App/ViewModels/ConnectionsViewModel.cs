using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Bydlo.App.Services;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class ConnectionsViewModel(MainViewModel shell) : PageViewModel(shell)
{
    public ObservableCollection<Subscription> Subscriptions { get; } = [];

    [ObservableProperty]
    private string _importText = "";

    [ObservableProperty]
    private string _subscriptionUrl = "";

    [ObservableProperty]
    private string _subscriptionName = "";

    [ObservableProperty]
    private bool _isWorking;

    public bool IsEmpty => Shell.Profiles.Count == 0;

    public override void OnShown()
    {
        Subscriptions.ReplaceWith(Services.Profiles.Subscriptions);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        await Shell.PasteImportCommand.ExecuteAsync(null);
    }

    public Task AddImportedAsync(ImportResult result) => AddAsync(result, null);

    public async Task ImportExternalSubscriptionAsync(string url)
    {
        var result = await Services.Subscriptions.FetchAsync(url);
        if (result.IsEmpty)
        {
            throw new InvalidOperationException(L["import.none"]);
        }

        var sub = await Services.Profiles.AddSubscriptionAsync(new Uri(url).Host, url);
        await Services.Profiles.ReplaceSubscriptionProfilesAsync(sub.Id, result.Profiles);
        Shell.ReloadProfiles();
        Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, result.Profiles.Count));
        OnShown();
    }

    [RelayCommand]
    private Task ImportFromText() => ImportAsync(ImportText, null);

    [RelayCommand]
    private async Task ImportFromFile()
    {
        var storage = Services.TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L["import.file"],
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("VPN configs") { Patterns = ["*.conf", "*.ovpn", "*.txt"] },
                FilePickerFileTypes.All,
            ],
        });
        await Shell.PreviewExternalAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    [RelayCommand]
    private async Task ImportFromQrImage()
    {
        var storage = Services.TopLevel()?.StorageProvider;
        if (storage is null)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L["import.qr"],
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        await Shell.PreviewExternalAsync([path]);
    }

    [RelayCommand]
    private async Task AddSubscription()
    {
        var url = SubscriptionUrl.Trim();
        if (!ProfileImporter.LooksLikeSubscriptionUrl(url))
        {
            Shell.ShowToast(L["import.sub.invalid"], isError: true);
            return;
        }

        var name = string.IsNullOrWhiteSpace(SubscriptionName) ? new Uri(url).Host : SubscriptionName.Trim();
        var sub = await Services.Profiles.AddSubscriptionAsync(name, url);
        SubscriptionUrl = "";
        SubscriptionName = "";
        await RefreshSubscription(sub);
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

    private async Task ImportAsync(string text, Guid? subscription)
    {
        if (ProfileImporter.LooksLikeSubscriptionUrl(text))
        {
            SubscriptionUrl = text.Trim();
            await AddSubscription();
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
            ImportText = "";
            Shell.ShowToast(Loc.T("import.added", added.Count) + (result.Errors.Count > 0 ? " " + Loc.T("import.skipped", result.Errors.Count) : ""));
        }
        else
        {
            Shell.ShowToast((result.Errors.Count > 0 ? result.Errors[0] : null) ?? L["import.none"], isError: true);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
