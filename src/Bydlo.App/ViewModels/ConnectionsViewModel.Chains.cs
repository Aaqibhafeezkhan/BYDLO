using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>Relay chain rows (issue #75): create, edit, connect, delete.</summary>
public sealed partial class ConnectionsViewModel
{
    [RelayCommand]
    private void NewChain() => Shell.NewChainCommand.Execute(null);

    [RelayCommand]
    private void EditChain(ChainItem item) => Shell.EditChainCommand.Execute(item);

    [RelayCommand]
    private async Task ConnectChain(ChainItem item)
    {
        Shell.SelectedChain = item;
        Shell.Navigate("home");
        if (!Shell.Home.IsConnected)
        {
            await Shell.Home.ToggleConnectionCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task ToggleChainFavorite(ChainItem item)
    {
        var updated = new ProxyChain
        {
            Id = item.Id,
            Name = item.Chain.Name,
            ProfileIds = [.. item.Chain.ProfileIds],
            PreferredEngine = item.Chain.PreferredEngine,
            AddedAt = item.Chain.AddedAt,
            IsFavorite = !item.Chain.IsFavorite,
        };
        await Shell.Guard(async () =>
        {
            await Services.Profiles.UpdateChainAsync(updated);
            Shell.ReloadProfiles();
        });
    }

    [RelayCommand]
    private async Task DeleteChain(ChainItem item)
    {
        var session = Services.Connections.Primary;
        if (session.HoldsResources && session.Chain?.Id == item.Id)
        {
            Shell.ShowToast(L["chain.delete.active"], isError: true);
            return;
        }

        await Shell.Guard(async () =>
        {
            await Services.Profiles.RemoveChainAsync(item.Id);
            if (Shell.SelectedChain?.Id == item.Id)
            {
                Shell.SelectedChain = null;
            }

            Shell.ReloadProfiles();
            Shell.ShowToast(L["chain.deleted"]);
        });
    }

    /// <summary>Resolves chain hops with their secrets for <see cref="ConnectionManager"/>.</summary>
    public async Task<IReadOnlyList<ChainHop>> ResolveChainAsync(ProxyChain chain, CancellationToken ct = default)
    {
        var hops = new List<ChainHop>(chain.ProfileIds.Count);
        foreach (var id in chain.ProfileIds)
        {
            var profile = Services.Profiles.Profiles.FirstOrDefault(p => p.Id == id)
                ?? throw new EngineException("One of the chain servers no longer exists.", localizationKey: "error.chain.unknown");
            hops.Add(new ChainHop(profile, await Services.Profiles.Secrets.LoadAsync(id.ToString(), ct).ConfigureAwait(false)));
        }

        return hops;
    }
}
