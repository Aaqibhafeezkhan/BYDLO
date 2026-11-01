using Bydlo.Core.Models;

namespace Bydlo.Core.Engines;

/// <summary>
/// Knows every engine adapter and picks one for a profile. In Mock Mode every request resolves to the mock factory.
/// </summary>
public sealed class EngineRegistry
{
    private readonly List<(EngineDescriptor Descriptor, Func<IEngine> Factory, int Priority)> _engines = [];
    private readonly Func<IEngine> _mockFactory;

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
        return Create(kind, settings);
    }

    public IEngine Create(EngineKind kind, AppSettings settings)
    {
        if (settings.MockMode || kind == EngineKind.Mock)
        {
            return _mockFactory();
        }

        var entry = _engines.FirstOrDefault(e => e.Descriptor.Kind == kind);
        return entry.Factory is null ? throw new EngineException($"Engine {kind} is not registered.") : entry.Factory();
    }

    /// <summary>Some option combinations only work with one engine (e.g. XHTTP transport needs Xray).</summary>
    private static EngineKind? ProtocolNeeds(Profile profile)
    {
        var transport = profile.Option(Keys.Transport);
        if (transport is not null && (transport.Equals("xhttp", StringComparison.OrdinalIgnoreCase) || transport.Equals("splithttp", StringComparison.OrdinalIgnoreCase)))
        {
            return EngineKind.Xray;
        }

        return null;
    }
}
