using Bydlo.Core.Config;
using Bydlo.Core.Models;

namespace Bydlo.Core.Engines;

/// <summary>
/// Knows every engine adapter and picks one for a profile. In Mock Mode every request resolves to the mock factory.
/// </summary>
public sealed class EngineRegistry
{
    private readonly List<(EngineDescriptor Descriptor, Func<IEngine> Factory, int Priority)> _engines = [];
    private readonly Func<IEngine> _mockFactory;

    public Func<IEngine, AppSettings, IEngine>? Decorate { get; set; }

    public EngineRegistry(Func<IEngine> mockFactory)
    {
        _mockFactory = mockFactory;
    }

    public IReadOnlyList<EngineDescriptor> Descriptors => [.. _engines.Select(e => e.Descriptor)];

    /// <summary>Registers an engine. Lower <paramref name="priority"/> wins when several engines support a protocol.</summary>
    public EngineRegistry Register(EngineDescriptor descriptor, Func<IEngine> factory, int priority = 100)
    {
        _engines.Add((descriptor, factory, priority));
        return this;
    }

    public EngineDescriptor? Find(EngineKind kind) => _engines.FirstOrDefault(e => e.Descriptor.Kind == kind).Descriptor;

    /// <summary>Engine kinds able to carry this profile on the given OS, best first.</summary>
    public IReadOnlyList<EngineKind> CandidatesFor(Profile profile, OsFamily os) =>
        [.. _engines
            .Where(e => !e.Descriptor.IsTool && e.Descriptor.Protocols.Contains(profile.Protocol))
            .Where(e => e.Descriptor.SupportOn(os) != SupportLevel.Unsupported)
            .Where(e => ProtocolNeeds(profile) is not { } required || required == e.Descriptor.Kind)
            .OrderBy(e => e.Descriptor.SupportOn(os) == SupportLevel.Implemented ? 0 : 1)
            .ThenBy(e => e.Priority)
            .Select(e => e.Descriptor.Kind)];

    public IEngine CreateFor(Profile profile, AppSettings settings, OsFamily os)
    {
        if (settings.MockMode)
        {
            return _mockFactory();
        }

        var candidates = CandidatesFor(profile, os);
        var kind = profile.PreferredEngine is { } preferred && candidates.Contains(preferred)
            ? preferred
            : candidates.Count > 0 ? candidates[0] : throw new EngineException($"No engine supports {profile.Protocol} on {os}.");
        if (settings.KillSwitch && settings.Mode == ConnectionMode.Tun && kind is not (EngineKind.SingBox or EngineKind.Xray))
        {
            throw new EngineException("error.killswitch.engine");
        }

        if (settings.Routing.Preset != RoutingPreset.All && kind != EngineKind.SingBox)
        {
            throw new EngineException("routing.preset.singbox", localizationKey: "routing.preset.singbox");
        }
        if (settings.Routing.Preset != RoutingPreset.All && settings.KillSwitch && settings.Mode == ConnectionMode.Tun)
        {
            throw new EngineException("routing.preset.killswitch", localizationKey: "routing.preset.killswitch");
        }

        return Create(kind, settings);
    }

    /// <summary>Chain-capable engine kinds able to carry every hop on the given OS, best first. Raw JSON configs are never chainable (issue #187).</summary>
    public IReadOnlyList<EngineKind> CandidatesForChain(IReadOnlyList<Profile> hops, OsFamily os)
    {
        var required = hops.Select(ProtocolNeeds).Where(r => r is not null).Distinct().ToList();
        return [.. _engines
            .Where(e => !e.Descriptor.IsTool && (e.Descriptor.Kind is EngineKind.SingBox or EngineKind.Xray))
            .Where(e => hops.All(h => e.Descriptor.Kind switch
            {
                EngineKind.SingBox => SingBoxConfigBuilder.ChainSupported.Contains(h.Protocol),
                EngineKind.Xray => XrayConfigBuilder.ChainSupported.Contains(h.Protocol),
                _ => false,
            }))
            .Where(e => e.Descriptor.SupportOn(os) != SupportLevel.Unsupported)
            .Where(e => required.Count == 0 || required.Count == 1 && required[0] == e.Descriptor.Kind)
            .OrderBy(e => e.Descriptor.SupportOn(os) == SupportLevel.Implemented ? 0 : 1)
            .ThenBy(e => e.Priority)
            .Select(e => e.Descriptor.Kind)];
    }

    public IEngine CreateForChain(IReadOnlyList<Profile> hops, EngineKind? preferred, AppSettings settings, OsFamily os)
    {
        if (settings.MockMode)
        {
            return _mockFactory();
        }

        var candidates = CandidatesForChain(hops, os);
        if (candidates.Count == 0)
        {
            throw new EngineException("No chain-capable engine supports every hop on this OS.", localizationKey: "error.chain.engine");
        }

        var kind = candidates[0];
        if (preferred is { } wanted)
        {
            // An explicit engine that cannot carry the chain is an honest NotImplemented, not a silent fallback.
            if (!candidates.Contains(wanted))
            {
                throw new EngineException($"Engine {wanted} cannot carry this chain.", localizationKey: "error.chain.engine");
            }

            kind = wanted;
        }

        if (settings.KillSwitch && settings.Mode == ConnectionMode.Tun && kind is not (EngineKind.SingBox or EngineKind.Xray))
        {
            throw new EngineException("error.killswitch.engine");
        }

        if (settings.Routing.Preset != RoutingPreset.All && kind != EngineKind.SingBox)
        {
            throw new EngineException("routing.preset.singbox", localizationKey: "routing.preset.singbox");
        }
        if (settings.Routing.Preset != RoutingPreset.All && settings.KillSwitch && settings.Mode == ConnectionMode.Tun)
        {
            throw new EngineException("routing.preset.killswitch", localizationKey: "routing.preset.killswitch");
        }

        return Create(kind, settings);
    }

    public IEngine Create(EngineKind kind, AppSettings settings)
    {
        if (settings.MockMode || kind == EngineKind.Mock)
        {
            return _mockFactory();
        }

        var entry = _engines.FirstOrDefault(e => e.Descriptor.Kind == kind);
        if (entry.Factory is null) { throw new EngineException($"Engine {kind} is not registered."); }
        var engine = entry.Factory();
        return Decorate?.Invoke(engine, settings) ?? engine;
    }

    /// <summary>Some option combinations only work with one engine (e.g. XHTTP transport needs Xray).</summary>
    private static EngineKind? ProtocolNeeds(Profile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Option(Keys.PinnedPeerCertSha256)) ||
            !string.IsNullOrWhiteSpace(profile.Option(Keys.VerifyPeerCertByName)))
        {
            return EngineKind.Xray;
        }

        var transport = profile.Option(Keys.Transport);
        if (transport is not null && (transport.Equals("xhttp", StringComparison.OrdinalIgnoreCase) || transport.Equals("splithttp", StringComparison.OrdinalIgnoreCase)))
        {
            return EngineKind.Xray;
        }

        if (transport is not null && (transport.Equals("kcp", StringComparison.OrdinalIgnoreCase) || transport.Equals("mkcp", StringComparison.OrdinalIgnoreCase)))
        {
            return EngineKind.Xray;
        }

        var headerType = profile.Option(Keys.HeaderType);
        if ((transport is null || transport.Equals("tcp", StringComparison.OrdinalIgnoreCase) || transport.Equals("raw", StringComparison.OrdinalIgnoreCase)) &&
            headerType is not null && headerType.Equals("http", StringComparison.OrdinalIgnoreCase))
        {
            return EngineKind.Xray;
        }

        var encryption = profile.Option(Keys.Encryption);
        if (profile.Protocol == ProtocolKind.Vless && encryption is not null && !encryption.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return EngineKind.Xray;
        }

        // Xray removed HTTP/2 transport; sing-box still carries h2/http (issue #242).
        if (transport is not null && (transport.Equals("h2", StringComparison.OrdinalIgnoreCase) || transport.Equals("http", StringComparison.OrdinalIgnoreCase)))
        {
            return EngineKind.SingBox;
        }

        // sing-box carries TLS-wrapped HTTP/SOCKS and Shadowsocks plugins; Xray silently drops them (issue #242).
        if (profile.Protocol is ProtocolKind.Http or ProtocolKind.Socks5 && profile.Option(Keys.Tls) == "true")
        {
            return EngineKind.SingBox;
        }

        if (profile.Protocol == ProtocolKind.Shadowsocks && profile.Option(Keys.Plugin) is not null)
        {
            return EngineKind.SingBox;
        }

        return null;
    }
}
