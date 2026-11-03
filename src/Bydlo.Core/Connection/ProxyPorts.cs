using Bydlo.Core.Models;

namespace Bydlo.Core.Connection;

/// <summary>Single place for the local proxy HTTP port rule (issue #207): Xray listens on SOCKS <c>LocalProxyPort</c> and HTTP <c>LocalProxyPort + 1</c>.</summary>
public static class ProxyPorts
{
    public const int MaxLocalProxyPort = 65534;

    public static bool NeedsAdjacentHttpPort(EngineKind kind) => kind == EngineKind.Xray;

    public static int HttpPort(int localProxyPort, EngineKind kind) =>
        NeedsAdjacentHttpPort(kind) ? checked(localProxyPort + 1) : localProxyPort;

    public static int HttpPort(AppSettings settings, EngineKind kind) =>
        HttpPort(settings.LocalProxyPort, kind);
}
