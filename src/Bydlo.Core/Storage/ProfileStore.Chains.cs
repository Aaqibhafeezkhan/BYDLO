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
            var next = new ProfileDocument { Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains, chain] };
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
            _doc.Chains[i] = chain;
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
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
            if (_doc.Chains.RemoveAll(c => c.Id == id) > 0)
            {
                await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
            }
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
            chain.Name = name.Trim();
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Detaches a deleted profile from every chain; drops chains that fall below two hops.</summary>
    private void DetachFromChainsUnsafe(Guid profileId)
    {
        foreach (var chain in _doc.Chains)
        {
            chain.ProfileIds.RemoveAll(id => id == profileId);
        }

        _doc.Chains.RemoveAll(c => c.ProfileIds.Count < ChainValidator.MinHops);
    }

    /// <summary>Every hop must be chainable by at least one common engine (sing-box or Xray).</summary>
    private void ThrowIfUnchainable(ProxyChain chain)
    {
        var protocols = chain.ProfileIds.Select(id => FindStored(id)!.Protocol).ToList();
        var ok = protocols.All(SingBoxConfigBuilder.Supported.Contains) || protocols.All(XrayConfigBuilder.Supported.Contains);
        if (!ok)
        {
            throw new EngineException("These servers cannot be chained with one engine.", localizationKey: "error.chain.engine");
        }
    }

    private Profile? FindStored(Guid id) => _doc.Profiles.FirstOrDefault(p => p.Id == id);
}
