using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.Core.Security;

/// <summary>The privileged boundary accepts profile data, never client-owned files or program paths.</summary>
public static class HelperRequestPolicy
{
    public static bool Supports(EngineKind kind) => kind is EngineKind.SingBox or EngineKind.Zapret or EngineKind.AmneziaWG or EngineKind.OpenVpn;

    public static void Validate(HelperRequest request)
    {
        if (request.Version != HelperProtocol.Version || !Enum.IsDefined(request.Operation) || !Supports(request.Engine))
        { throw new InvalidDataException("Unsupported helper request."); }
        ValidateSettings(request.Settings);
        if (request.ConnectionId is { Length: > 256 } || request.Operation == HelperOperation.CloseConnection && string.IsNullOrEmpty(request.ConnectionId))
        { throw new InvalidDataException("Invalid connection id."); }
        if (request.Operation is not (HelperOperation.Start or HelperOperation.Reconnect)) { return; }
        var start = request.Start ?? throw new InvalidDataException("Missing start request.");
        ValidateSettings(start.Settings);
        if (start.RuleSetDirectory is not null || start.Chain is not null)
        { throw new InvalidDataException("External rule sets and relay chains are unavailable in helper mode."); }
        if (start.Group is { } group)
        {
            var members = start.GroupMembers ?? throw new InvalidDataException("Missing group members.");
            Config.ServerGroupSupport.Validate(group, members, request.Engine);
            if (members.Any(m => m.Profile.Option(Keys.Plugin) is not null))
            { throw new InvalidDataException("Executable plugins are unavailable in helper mode."); }
        }
        else if (start.GroupMembers is not null) { throw new InvalidDataException("Missing group."); }
        if (request.Engine != EngineKind.Zapret)
        {
            var profile = start.Profile ?? throw new InvalidDataException("Missing profile.");
            if (profile.Protocol is ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig || profile.Option(Keys.Plugin) is not null)
            { throw new InvalidDataException("Custom configs and executable plugins are unavailable in helper mode."); }
            if (request.Engine == EngineKind.OpenVpn)
            { ValidateOpenVpn(start.Secrets[Keys.RawConfig] ?? ""); }
        }
    }

    private static void ValidateSettings(AppSettings settings)
    {
        if (settings.EnginePaths.Count != 0 || !string.IsNullOrEmpty(settings.Zapret.InstallPath) || settings.MockMode)
        { throw new InvalidDataException("Only bundled engines are allowed by the helper."); }
        if (settings.Routing.Preset != RoutingPreset.All || settings.Routing.RemoteDnsRuleSets.Count != 0 ||
            settings.Routing.DirectDnsRuleSets.Count != 0 || settings.Routing.OrderedRules.Any(r =>
                r.Condition is Routing.RoutingCondition.GeoSite or Routing.RoutingCondition.GeoIp))
        { throw new InvalidDataException("External rule sets are unavailable in helper mode."); }
        if (settings.Zapret.Strategy is { } strategy &&
            (strategy.Contains('/') || strategy.Contains('\\') || strategy.Contains(':') || strategy.Contains("..", StringComparison.Ordinal)))
        { throw new InvalidDataException("Invalid strategy name."); }
    }

    public static void ValidateOpenVpn(string config) => OpenVpnConfigPolicy.Validate(config);
}
