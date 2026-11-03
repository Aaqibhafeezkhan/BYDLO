using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Storage;

/// <summary>Proxy chain (relay) persistence. Chains reference profiles by id and hold no secrets.</summary>
public sealed partial class ProfileStore
{
    public IReadOnlyList<ProxyChain> Chains => _doc.Chains;

    public async Task<ProxyChain> AddChainAsync(string name, IEnumerable<Guid> profileIds, EngineKind? preferredEngine = null, CancellationToken ct = default)
    {
        var chain = new ProxyChain { Name = name.Trim(), ProfileIds = profileIds.ToList(), PreferredEngine = preferredEngine };
        if (string.IsNullOrWhiteSpace(chain.Name))
        {
            throw new EngineException("Chain name must not be empty.", localizationKey: "error.chain.name");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ChainValidator.ThrowIfInvalid(chain, FindStored);
            ThrowIfUnchainable(chain);
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains, chain] };
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
            return chain;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateChainAsync(ProxyChain chain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chain.Name))
        {
            throw new EngineException("Chain name must not be empty.", localizationKey: "error.chain.name");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var i = _doc.Chains.FindIndex(c => c.Id == chain.Id);
            if (i < 0)
            {
                throw new InvalidDataException("Chain not found.");
            }

            ChainValidator.ThrowIfInvalid(chain, FindStored);
            ThrowIfUnchainable(chain);
            // Detached snapshot: publish only after the atomic write succeeds (issue #188).
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
            next.Chains[i] = CopyChain(chain);
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveChainAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var remaining = _doc.Chains.Where(c => c.Id != id).ToList();
            if (remaining.Count == _doc.Chains.Count)
            {
                return;
            }

            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = remaining };
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameChainAsync(Guid id, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new EngineException("Chain name must not be empty.", localizationKey: "error.chain.name");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var chain = _doc.Chains.FirstOrDefault(c => c.Id == id) ?? throw new InvalidDataException("Chain not found.");
            var renamed = CopyChain(chain);
            renamed.Name = name.Trim();
            var next = new ProfileDocument { Version = _doc.Version, PendingSecretDeletes = [.. _doc.PendingSecretDeletes], Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
            next.Chains[next.Chains.FindIndex(c => c.Id == id)] = renamed;
            await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
            _doc = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Detaches a chain from the live document so a failed write leaves memory == disk.</summary>
    private static ProxyChain CopyChain(ProxyChain chain) => new()
    {
        Id = chain.Id, Name = chain.Name, ProfileIds = [.. chain.ProfileIds],
        PreferredEngine = chain.PreferredEngine, AddedAt = chain.AddedAt, IsFavorite = chain.IsFavorite,
    };

    /// <summary>Every hop must be chainable by at least one common engine (sing-box or Xray). Raw JSON configs have no chain branch (issue #187).</summary>
    private void ThrowIfUnchainable(ProxyChain chain)
    {
        var protocols = chain.ProfileIds.Select(id => FindStored(id)!.Protocol).ToList();
        var ok = protocols.All(SingBoxConfigBuilder.ChainSupported.Contains) || protocols.All(XrayConfigBuilder.ChainSupported.Contains);
        if (!ok)
        {
            throw new EngineException("These servers cannot be chained with one engine.", localizationKey: "error.chain.engine");
        }
    }

    private Profile? FindStored(Guid id) => _doc.Profiles.FirstOrDefault(p => p.Id == id);
}
