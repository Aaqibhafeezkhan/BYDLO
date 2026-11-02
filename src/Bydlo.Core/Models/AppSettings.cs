namespace Bydlo.Core.Models;

public enum ThemePreference
{
    System,
    Purple,
    Light,
    Oled,
}

/// <summary>External IP lookup service used by the "Check connection" action on Home.</summary>
public enum IpCheckProvider
{
    IpWhoIs,
    IpApiCo,
    IpInfo,
}

public enum MockScenario
{
    /// <summary>Connects after a short delay and produces synthetic traffic.</summary>
    Success,

    /// <summary>Fails while connecting (e.g. "handshake timeout").</summary>
    FailOnConnect,

    /// <summary>Connects, then the simulated process crashes after ~20 seconds.</summary>
    DropAfterConnect,

    /// <summary>Takes ~6 seconds to connect.</summary>
    SlowConnect,
}

public sealed class AppSettings
{
    public bool StartWithSystem { get; set; }

    public bool ConnectOnStartup { get; set; }

    public bool MinimizeToTray { get; set; }

    public bool ShowNotifications { get; set; } = true;

    public bool SuggestClipboardLinks { get; set; } = true;

    public ThemePreference Theme { get; set; } = ThemePreference.Purple;

    /// <summary>"auto", "en" or "ru".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// False only for a fresh installation. Existing settings without this field deserialize as true,
    /// so upgrading users are not shown the first-run screen again.
    /// </summary>
    public bool OnboardingCompleted { get; set; } = true;

    public bool MockMode { get; set; }

    /// <summary>Session override without changing the saved user preference.</summary>
    public AppSettings WithMockMode(bool enabled)
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.MockMode = enabled;
        return copy;
    }

    public static bool IsMockForced(IEnumerable<string> args, string? environmentValue) =>
        args.Contains("--mock", StringComparer.Ordinal) || environmentValue == "1";

    public MockScenario MockScenario { get; set; } = MockScenario.Success;

    public Guid? LastProfileId { get; set; }

    public ConnectionMode Mode { get; set; } = ConnectionMode.LocalProxy;

    /// <summary>Port of the local mixed (SOCKS5 + HTTP) inbound.</summary>
    public int LocalProxyPort { get; set; } = 2080;

    /// <summary>Service used by the explicit "Check connection" action. The request always goes through the tunnel.</summary>
    public IpCheckProvider IpCheckProvider { get; set; } = IpCheckProvider.IpWhoIs;

    /// <summary>Sets the OS proxy to the local inbound while connected. Off by default.</summary>
    public bool SetSystemProxy { get; set; }

    /// <summary>Retain a separate strict TUN while the sing-box backend reconnects. Off by default.</summary>
    public bool KillSwitch { get; set; }

    public bool AutoReconnect { get; set; } = true;

    public bool ShowAdvanced { get; set; }

    public RoutingSettings Routing { get; set; } = new();

    public ZapretSettings Zapret { get; set; } = new();

    public TgWsProxySettings TgWsProxy { get; set; } = new();

    /// <summary>Explicit paths to engine executables, keyed by <see cref="EngineKind"/> name.</summary>
    public Dictionary<string, string> EnginePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RoutingSettings
{
    /// <summary>
    /// Keep CGNAT / mesh-VPN address ranges (100.64.0.0/10, fd7a:115c:a1e3::/48) outside the tunnel,
    /// so other VPN or mesh-network clients keep working while BYDLO is connected.
    /// </summary>
    public bool PreserveMeshVpnRanges { get; set; } = true;

    public bool BypassPrivateNetworks { get; set; } = true;

    /// <summary>DNS-over-HTTPS server used through the tunnel.</summary>
    public string RemoteDns { get; set; } = "1.1.1.1";

    public string DirectDns { get; set; } = "local";

    public List<string> DirectDomains { get; set; } = [];

    public List<string> ProxyDomains { get; set; } = [];

    public List<string> BlockDomains { get; set; } = [];

    public List<string> DirectCidrs { get; set; } = [];

    /// <summary>When true only <see cref="ProxyDomains"/> go through the tunnel (split tunneling "include" mode).</summary>
    public bool ProxyOnlyListed { get; set; }

    /// <summary>Process names that bypass the tunnel (TUN mode, sing-box only).</summary>
    public List<string> DirectProcesses { get; set; } = [];
}

public sealed class ZapretSettings
{
    public List<string> TestTargets { get; set; } = ["https://www.youtube.com/", "https://discord.com/"];

    /// <summary>Fallback folder with an extracted Flowseal release or a native nfqws/tpws executable.</summary>
    public string? InstallPath { get; set; }

    /// <summary>Flowseal batch file name or native preset id. Empty selects the platform default.</summary>
    public string? Strategy { get; set; }

    /// <summary>Game filter: "disabled", "all", "tcp" or "udp".</summary>
    public string GameFilter { get; set; } = "disabled";
}

public sealed class TgWsProxySettings
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 1443;

    public List<string> DcIps { get; set; } = ["2:149.154.167.220", "4:149.154.167.220"];

    public string FakeTlsDomain { get; set; } = "";

    public bool Verbose { get; set; }

    public bool CfProxyFallback { get; set; } = true;

    /// <summary>Own Cloudflare-proxied domains for the fallback. Empty uses the list maintained by Flowseal.</summary>
    public List<string> CfProxyDomains { get; set; } = [];
}
