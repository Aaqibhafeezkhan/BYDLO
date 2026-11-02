using System.Collections.ObjectModel;
using Bydlo.App.Services;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>One built-in public list with localized display strings (issue #39).</summary>
public sealed class CatalogRow(PublicSubscriptionSource source) : ObservableObject
{
    public string Id => source.Id;

    public string Name => Loc.Instance[source.NameKey];

    public string Description => Loc.Instance[source.DescriptionKey];
}

/// <summary>Built-in catalog of free community subscription lists (issue #39).</summary>
public sealed partial class ConnectionsViewModel
{
    public ObservableCollection<CatalogRow> CatalogSources { get; } = [];

    private bool _catalogSubscribed;

    public bool CanAddCatalog => !IsWorking;

    partial void OnIsWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddCatalog));
        OnPropertyChanged(nameof(CanRegisterWarp));
    }

    private void RefreshCatalog()
    {
        CatalogSources.ReplaceWith(PublicSubscriptionCatalog.Sources.Select(s => new CatalogRow(s)));
        if (!_catalogSubscribed)
        {
            _catalogSubscribed = true;
            L.PropertyChanged += (_, _) => RefreshCatalog();
        }
    }

    [RelayCommand]
    private async Task AddCatalogSource(string id)
    {
        var source = PublicSubscriptionCatalog.Sources.FirstOrDefault(s => s.Id == id);
        if (source is null || IsWorking)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await Shell.Guard(async () =>
            {
                if (Services.IsMock || NetworkSafetyGuard.IsLocked)
                {
                    throw new EngineException("Real downloads are disabled.", localizationKey: "catalog.locked");
                }
                var (preview, url) = await Services.Subscriptions.FetchFirstAsync(source.Urls, Services.ShutdownToken);
                var result = preview.Import;
                if (result.IsEmpty)
                {
                    Shell.ShowToast((result.Errors.Count > 0 ? result.Errors[0] : null) ?? L["import.none"], isError: true);
                    return;
                }

                var row = CatalogSources.FirstOrDefault(r => r.Id == id);
                var sub = await Services.Profiles.AddSubscriptionWithProfilesAsync(
                    row?.Name ?? new Uri(url ?? source.Urls[0]).Host, url ?? source.Urls[0], result.Profiles);
                Shell.ReloadProfiles();
                Shell.ShowToast(Loc.T("import.sub.updated", sub.Name, result.Profiles.Count));
                OnShown();
            });
        }
        finally { IsWorking = false; }
    }
}
