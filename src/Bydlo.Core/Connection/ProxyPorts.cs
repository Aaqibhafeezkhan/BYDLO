using Bydlo.Core.Models;

namespace Bydlo.Core.Connection;

/// <summary>Single place for the local proxy HTTP port rule (issue #207): Xray listens on SOCKS <c>LocalProxyPort</c> and HTTP <c>LocalProxyPort + 1</c>.</summary>
public static class ProxyPorts
{
    public const int MaxLocalProxyPort = 65534;

    public static bool NeedsAdjacentHttpPort(EngineKind kind) => kind == EngineKind.Xray;

    /// <summary>
    /// Whether the primary tunnel binds a TCP listener on <c>LocalProxyPort</c> (issues #353, #380).
    /// Proxy adapters in LocalProxy mode bind; TUN binds only through the retained LocalProxy backend
    /// (TUN + Kill Switch, see <c>RetainedTunEngine</c>). Plain TUN carries traffic in a TUN
    /// inbound (<c>SingBoxConfigBuilder.BuildInbound</c>) and owns no TCP port.
    /// </summary>
    public static bool BindsLocalTcpPort(AppSettings settings) =>
        BindsLocalTcpPort(settings.Mode, settings.KillSwitch);

    public static bool BindsLocalTcpPort(ConnectionMode mode, bool killSwitch) =>
        mode == ConnectionMode.LocalProxy || (mode == ConnectionMode.Tun && killSwitch);

    /// <summary>Native tunnel adapters do not own a SOCKS/HTTP listener, regardless of the selected mode.</summary>
    public static bool BindsLocalTcpPort(ConnectionMode mode, bool killSwitch, EngineKind? engine) =>
        engine is EngineKind.SingBox or EngineKind.Xray && BindsLocalTcpPort(mode, killSwitch);

    public static int HttpPort(int localProxyPort, EngineKind kind) =>
        NeedsAdjacentHttpPort(kind) ? checked(localProxyPort + 1) : localProxyPort;

    public static int HttpPort(AppSettings settings, EngineKind kind) =>
        HttpPort(settings.LocalProxyPort, kind);
}
