namespace Bydlo.Core.Models;

/// <summary>
/// An ordered list of existing profiles forming a proxy chain (relay).
/// Traffic enters at the first hop and leaves at the last one.
/// Hop credentials stay in <see cref="Security.ISecretStore"/> under each profile id;
/// a chain itself holds no secrets.
/// </summary>
public sealed class ProxyChain
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Hop profile ids, entry first and exit last.</summary>
    public List<Guid> ProfileIds { get; set; } = [];

    /// <summary>Forces a chain-capable engine; <c>null</c> lets <see cref="Engines.EngineRegistry"/> pick.</summary>
    public EngineKind? PreferredEngine { get; set; }

    public DateTimeOffset AddedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsFavorite { get; set; }
}

/// <summary>One resolved chain hop: profile plus its secrets.</summary>
public sealed record ChainHop(Profile Profile, ProfileSecrets Secrets);

/// <summary>Structural validation for proxy chains. Engine support is checked by the config builders.</summary>
public static class ChainValidator
{
    public const int MinHops = 2;

    public const int MaxHops = 5;

    /// <summary>Returns localization keys for every problem found; empty when the chain is valid.</summary>
    public static IReadOnlyList<string> Validate(ProxyChain chain, Func<Guid, Profile?> lookup)
    {
        var errors = new List<string>();
        if (chain.ProfileIds.Count < MinHops)
        {
            errors.Add("error.chain.toofew");
        }

        if (chain.ProfileIds.Count > MaxHops)
        {
            errors.Add("error.chain.toolong");
        }

        if (chain.ProfileIds.Distinct().Count() != chain.ProfileIds.Count)
        {
            errors.Add("error.chain.duplicate");
        }

        if (chain.ProfileIds.Any(id => id == Guid.Empty || lookup(id) is null))
        {
            errors.Add("error.chain.unknown");
        }
        if (chain.PreferredEngine is { } kind && kind is not (EngineKind.SingBox or EngineKind.Xray))
        {
            errors.Add("error.chain.engine");
        }

        return errors;
    }

    /// <summary>Throws <see cref="Engines.EngineException"/> with a localization key on the first problem.</summary>
    public static void ThrowIfInvalid(ProxyChain chain, Func<Guid, Profile?> lookup)
    {
        var errors = Validate(chain, lookup);
        if (errors.Count > 0)
        {
            throw new Engines.EngineException("Invalid proxy chain.", localizationKey: errors[0]);
        }
    }
}
