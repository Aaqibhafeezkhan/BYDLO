using System.Collections.ObjectModel;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Full relay-chain editor (issue #75): name, ordered hops, engine override.</summary>
public sealed partial class ChainEditorViewModel(MainViewModel shell) : PageViewModel(shell)
{
    [ObservableProperty] private Guid? _editingId;

    [ObservableProperty] private string _name = "";

    /// <summary>Ordered hops, entry first.</summary>
    public ObservableCollection<ProfileItem> Hops { get; } = [];

    /// <summary>Servers that can still be appended (already used ones are hidden).</summary>
    public ObservableCollection<ProfileItem> AvailableServers { get; } = [];

    [ObservableProperty] private ProfileItem? _pickedServer;

    /// <summary>0 = Auto, 1 = sing-box, 2 = Xray.</summary>
    [ObservableProperty] private int _engineIndex;

    [ObservableProperty] private bool _isSaving;

    public bool IsNew => EditingId is null;

    public bool HasHops => Hops.Count > 0;

    public bool CanAddHop => PickedServer is not null;

    partial void OnPickedServerChanged(ProfileItem? value) => OnPropertyChanged(nameof(CanAddHop));

    public void Create()
    {
        EditingId = null;
        Name = "";
        EngineIndex = 0;
        RefreshLists([]);
        OnPropertyChanged(nameof(IsNew));
    }

    public void Edit(ChainItem item)
    {
        EditingId = item.Id;
        Name = item.Chain.Name;
        EngineIndex = item.Chain.PreferredEngine switch
        {
            EngineKind.SingBox => 1,
            EngineKind.Xray => 2,
            _ => 0,
        };
        RefreshLists(item.Chain.ProfileIds);
        OnPropertyChanged(nameof(IsNew));
    }

    private void RefreshLists(IReadOnlyList<Guid> ids)
    {
        var byId = Services.Profiles.Profiles.ToDictionary(p => p.Id);
        Hops.ReplaceWith(ids.Where(byId.ContainsKey).Select(id => new ProfileItem(byId[id])));
        RefreshAvailable();
        OnPropertyChanged(nameof(HasHops));
    }

    private void RefreshAvailable()
    {
        var used = Hops.Select(h => h.Id).ToHashSet();
        AvailableServers.ReplaceWith(Services.Profiles.Profiles
            .Where(p => !used.Contains(p.Id))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ProfileItem(p)));
        if (PickedServer is not null && used.Contains(PickedServer.Id))
        {
            PickedServer = null;
        }

        OnPropertyChanged(nameof(CanAddHop));
    }

    [RelayCommand]
    private void AddHop()
    {
        if (PickedServer is null)
        {
            return;
        }

        Hops.Add(new ProfileItem(PickedServer.Profile));
        PickedServer = null;
        RefreshAvailable();
        OnPropertyChanged(nameof(HasHops));
    }

    [RelayCommand]
    private void RemoveHop(ProfileItem row)
    {
        Hops.Remove(row);
        RefreshAvailable();
        OnPropertyChanged(nameof(HasHops));
    }

    [RelayCommand]
    private void MoveHopUp(ProfileItem row)
    {
        var i = Hops.IndexOf(row);
        if (i > 0)
        {
            Hops.Move(i, i - 1);
        }
    }

    [RelayCommand]
    private void MoveHopDown(ProfileItem row)
    {
        var i = Hops.IndexOf(row);
        if (i >= 0 && i < Hops.Count - 1)
        {
            Hops.Move(i, i + 1);
        }
    }

    [RelayCommand]
    private void Close() => Shell.IsChainEditorOpen = false;

    [RelayCommand]
    private async Task Save()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var ids = Hops.Select(h => h.Id).ToList();
            EngineKind? engine = EngineIndex switch
            {
                1 => EngineKind.SingBox,
                2 => EngineKind.Xray,
                _ => null,
            };
            var editing = EditingId;
            var name = Name;
            await Shell.Guard(async () =>
            {
                if (editing is { } id)
                {
                    var existing = Services.Profiles.Chains.FirstOrDefault(c => c.Id == id)
                        ?? throw new EngineException("Chain no longer exists.", localizationKey: "error.chain.unknown");
                    // Clone: the stored instance must not change unless the update succeeds.
                    var updated = new ProxyChain
                    {
                        Id = existing.Id,
                        Name = name.Trim(),
                        ProfileIds = ids,
                        PreferredEngine = engine,
                        AddedAt = existing.AddedAt,
                        IsFavorite = existing.IsFavorite,
                    };
                    await Services.Profiles.UpdateChainAsync(updated);
                }
                else
                {
                    await Services.Profiles.AddChainAsync(name, ids, engine);
                }

                Shell.ReloadProfiles();
                Shell.IsChainEditorOpen = false;
                Shell.ShowToast(L["chain.saved"]);
            });
        }
        finally
        {
            IsSaving = false;
        }
    }
}
