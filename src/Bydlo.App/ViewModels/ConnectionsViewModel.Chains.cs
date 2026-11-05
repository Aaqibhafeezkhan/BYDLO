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
        // ConnectionManager.ConnectChainAsync validates before stopping the live
        // session (issue #266). Always switch, even when another profile or chain
        // is active; staying on the old session while showing the new selection
        // is the bug reported in issue #333.
        var alreadyActive = Shell.Home.IsConnected
            && Shell.Primary.Chain?.Id == item.Id;
        var previousProfile = Shell.SelectedProfile;
        var previousChain = Shell.SelectedChain;
        var previousUseGroup = Shell.Home.UseGroup;
        Shell.Home.UseGroup = false;
        Shell.SelectedProfile = null;
        Shell.SelectedChain = item;
        Shell.Navigate("home");
        if (alreadyActive)
        {
            return;
        }
        bool connected;
        try
        {
            connected = await Shell.Guard(async () =>
            {
                var chain = Services.Profiles.Chains.FirstOrDefault(c => c.Id == item.Id)
                    ?? throw new EngineException("One of the chain servers no longer exists.", localizationKey: "error.chain.unknown");
                var hops = await ResolveChainAsync(chain);
                foreach (var w in Services.Connections.PreviewChainConflicts(hops.Select(h => h.Profile).ToList(), Services.EffectiveSettings).Where(i => i.Severity == ConflictSeverity.Warning))
                {
                    Shell.ShowToast(w.Message);
                }

                await Services.Connections.ConnectChainAsync(chain, hops, Services.EffectiveSettings);
            });
        }
        catch
        {
            // A failed switch must not leave the new chain selected
            // while another session is active (issue #333).
            Shell.SelectedProfile = previousProfile;
            Shell.SelectedChain = previousChain;
            Shell.Home.UseGroup = previousUseGroup;
            throw;
        }
        if (!connected)
        {
            Shell.SelectedProfile = previousProfile;
            Shell.SelectedChain = previousChain;
            Shell.Home.UseGroup = previousUseGroup;
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
