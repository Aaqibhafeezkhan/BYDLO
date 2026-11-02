using Bydlo.Core.Models;
using Bydlo.Core.Platform;

namespace Bydlo.Core.Connection;

public enum ConflictSeverity
{
    Warning,
    Blocking,
}

public sealed record ConflictIssue(ConflictSeverity Severity, string Code, string Message);

/// <summary>Detects conflicts between the primary tunnel and tools before anything is started.</summary>
public sealed class ConflictDetector(IPortChecker ports)
{
    public IReadOnlyList<ConflictIssue> CheckPrimary(Profile profile, AppSettings settings, bool zapretRunning, bool tgRunning)
    {
        var issues = new List<ConflictIssue>();
        if (settings.MockMode)
        {
            return issues;
        }

        var usesLocalInbound = profile.Protocol is not (ProtocolKind.OpenVpn or ProtocolKind.AmneziaWG)
            && !(profile.Protocol == ProtocolKind.WireGuard && profile.PreferredEngine == EngineKind.WireGuard);

        if (usesLocalInbound && ports.IsTcpPortInUse(settings.LocalProxyPort))
        {
            issues.Add(new(ConflictSeverity.Blocking, "port.local-proxy",
                $"Local proxy port {settings.LocalProxyPort} is already in use. Change it in Settings → Advanced."));
        }

        if (tgRunning && settings.LocalProxyPort == settings.TgWsProxy.Port)
        {
            issues.Add(new(ConflictSeverity.Blocking, "port.tg-clash",
                $"TgWsProxy already listens on port {settings.TgWsProxy.Port}."));
        }

        if (zapretRunning && settings.Mode == ConnectionMode.Tun)
        {
            issues.Add(new(ConflictSeverity.Warning, "zapret.tun",
                "Zapret and a TUN tunnel both intercept traffic. Tunnel traffic is encrypted, so Zapret usually has no effect on it."));
        }

        if (settings.Mode == ConnectionMode.Tun && !settings.Routing.PreserveMeshVpnRanges)
        {
            issues.Add(new(ConflictSeverity.Warning, "tun.mesh-vpn",
                "TUN mode with 'Keep mesh VPN ranges direct' disabled may break other VPN / mesh-network clients."));
        }

        if (settings.Mode == ConnectionMode.Tun && settings.AllowLan)
        {
            issues.Add(new(ConflictSeverity.Warning, "lan.tun",
                "Allow LAN only affects Local proxy mode; TUN mode has no local proxy inbound to share."));
        }

        if (settings.AllowLan && string.IsNullOrWhiteSpace(settings.LocalProxyUsername))
        {
            issues.Add(new(ConflictSeverity.Blocking, "lan.login",
                "LAN access is on but the proxy login is empty. Set it in Settings → Advanced."));
        }

        return issues;
    }

    public IReadOnlyList<ConflictIssue> CheckTgWsProxy(AppSettings settings, bool primaryConnected)
    {
        var issues = new List<ConflictIssue>();
        if (settings.MockMode)
        {
            return issues;
        }

        var port = settings.TgWsProxy.Port;
        if (port is < 1 or > 65535)
        {
            issues.Add(new(ConflictSeverity.Blocking, "tg.port-range", $"Port {port} is invalid."));
        }
        else if (ports.IsTcpPortInUse(port))
        {
            issues.Add(new(ConflictSeverity.Blocking, "tg.port-busy",
                $"Port {port} is already in use by another program. Pick another port for TgWsProxy."));
        }

        if (primaryConnected && settings.LocalProxyPort == port)
        {
            issues.Add(new(ConflictSeverity.Blocking, "port.tg-clash", $"Port {port} is used by the active tunnel."));
        }

        return issues;
    }

    public IReadOnlyList<ConflictIssue> CheckZapret(AppSettings settings, bool primaryConnected)
    {
        var issues = new List<ConflictIssue>();
        if (settings.MockMode)
        {
            return issues;
        }

        if (primaryConnected && settings.Mode == ConnectionMode.Tun)
        {
            issues.Add(new(ConflictSeverity.Warning, "zapret.tun",
                "A TUN tunnel is active: Zapret will only affect traffic that bypasses the tunnel."));
        }

        return issues;
    }
}
