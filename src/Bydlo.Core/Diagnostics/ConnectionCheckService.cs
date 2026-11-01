using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>Result of an explicit connection check.</summary>
public sealed record IpCheckResult(string Ip, string? CountryCode, string? CountryName, TimeSpan Latency);

/// <summary>User-facing failure of the connection check.</summary>
public sealed class ConnectionCheckException : Exception
{
    public ConnectionCheckException(string message)
        : base(message)
    {
    }

    public ConnectionCheckException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Checks the external IP through the active tunnel. This is a read-only, user-initiated request:
/// nothing runs automatically, and in development (<c>BYDLO_SAFE_DEV=1</c>) real requests are refused.
/// </summary>
public sealed class ConnectionCheckService
{
    public const string ProxyHost = "127.0.0.1";

    private const string UserAgent = "BYDLO/0.1";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<int?, HttpMessageHandler>? _handlerFactory;

    public ConnectionCheckService()
    {
    }

    /// <summary>Used by tests to inject a fake HTTP handler. Never touches the network.</summary>
    public ConnectionCheckService(Func<int?, HttpMessageHandler> handlerFactory) =>
        _handlerFactory = handlerFactory ?? throw new ArgumentNullException(nameof(handlerFactory));

    /// <summary>Providers offered in Settings, in display order.</summary>
    public static IReadOnlyList<IpCheckProvider> Providers { get; } = Enum.GetValues<IpCheckProvider>();

    /// <summary>Domains forced through the proxy by the config builders so split tunneling cannot fake the result.</summary>
    public static IReadOnlyList<string> ProviderDomains { get; } = ["ipwho.is", "ipapi.co", "ipinfo.io"];

    /// <summary>
    /// Picks the HTTP proxy port for the active engine, or <c>null</c> when the request should go direct.
    /// sing-box exposes a mixed inbound; Xray uses the next port for its HTTP inbound. TUN and native
    /// WireGuard / OpenVPN tunnels carry the request without a local proxy.
    /// </summary>
    public static int? ResolveProxyPort(EngineKind? engine, ConnectionMode mode, int localProxyPort)
    {
        if (mode == ConnectionMode.Tun)
        {
            return null;
        }

        return engine switch
        {
            EngineKind.SingBox => localProxyPort,
            EngineKind.Xray => localProxyPort + 1,
            _ => null,
        };
    }

    public async Task<IpCheckResult> CheckAsync(IpCheckProvider provider, int? proxyPort, CancellationToken ct = default)
    {
        if (_handlerFactory is null)
        {
            NetworkSafetyGuard.EnsureAllowed("check the external IP");
        }

        if (proxyPort is { } port && port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyPort), "Proxy port must be between 1 and 65535.");
        }

        var uri = Endpoint(provider);
        var handler = _handlerFactory?.Invoke(proxyPort) ?? CreateHandler(proxyPort);
        using var http = new HttpClient(handler) { Timeout = RequestTimeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");

        var stopwatch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ConnectionCheckException($"The check service returned HTTP {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        stopwatch.Stop();

        var parsed = ParseResponse(provider, body);
        return parsed with { Latency = stopwatch.Elapsed };
    }

    /// <summary>Parses a provider response. Public for isolated parser tests.</summary>
    public static IpCheckResult ParseResponse(IpCheckProvider provider, string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ConnectionCheckException("The check service returned an empty response.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ConnectionCheckException("The check service returned an unexpected response.");
            }

            ThrowIfError(root);
            return provider switch
            {
                IpCheckProvider.IpWhoIs => ParseIpWhoIs(root),
                IpCheckProvider.IpApiCo => ParseIpApiCo(root),
                IpCheckProvider.IpInfo => ParseIpInfo(root),
                _ => throw new ArgumentOutOfRangeException(nameof(provider)),
            };
        }
        catch (JsonException ex)
        {
            throw new ConnectionCheckException("The check service returned invalid JSON.", ex);
        }
    }

    private static IpCheckResult ParseIpWhoIs(JsonElement root)
    {
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            throw new ConnectionCheckException(String(root, "message") ?? "The check service rejected the request.");
        }

        return Build(root, String(root, "country_code"), String(root, "country"));
    }

    private static IpCheckResult ParseIpApiCo(JsonElement root) =>
        Build(root, String(root, "country_code") ?? String(root, "country"), String(root, "country_name"));

    private static IpCheckResult ParseIpInfo(JsonElement root) =>
        Build(root, String(root, "country"), null);

    private static IpCheckResult Build(JsonElement root, string? countryCode, string? countryName)
    {
        var ip = String(root, "ip")?.Trim();
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out _))
        {
            throw new ConnectionCheckException("The check service did not return a valid IP address.");
        }

        var code = countryCode?.Trim().ToUpperInvariant();
        if (code is { Length: > 3 })
        {
            code = null;
        }

        var name = countryName?.Trim();
        return new IpCheckResult(ip, code, string.IsNullOrWhiteSpace(name) ? null : name, TimeSpan.Zero);
    }

    private static void ThrowIfError(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.False or JsonValueKind.Null)
        {
            return;
        }

        var message = error.ValueKind == JsonValueKind.Object
                      && error.TryGetProperty("message", out var detail)
                      && detail.ValueKind == JsonValueKind.String
            ? detail.GetString() ?? "The check service rejected the request."
            : String(root, "reason") ?? String(root, "message") ?? "The check service rejected the request.";
        throw new ConnectionCheckException(message);
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri Endpoint(IpCheckProvider provider) => provider switch
    {
        IpCheckProvider.IpWhoIs => new Uri("https://ipwho.is/"),
        IpCheckProvider.IpApiCo => new Uri("https://ipapi.co/json/"),
        IpCheckProvider.IpInfo => new Uri("https://ipinfo.io/json"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    internal static SocketsHttpHandler CreateHandler(int? proxyPort) =>
        new SocketsHttpHandler
        {
            UseProxy = proxyPort is not null,
            Proxy = proxyPort is { } port ? new WebProxy($"http://{ProxyHost}:{port}") : null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
        };
}
