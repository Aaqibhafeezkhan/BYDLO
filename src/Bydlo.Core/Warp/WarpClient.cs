using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Warp;

/// <summary>Options for Cloudflare WARP registration (issue #85).</summary>
public sealed record WarpOptions
{
    public string ApiBase { get; init; } = "https://api.cloudflareclient.com";

    /// <summary>Client API version, e.g. <c>v0a1922</c>. Bumped only after re-testing.</summary>
    public string ApiVersion { get; init; } = "v0a1922";

    public string UserAgent { get; init; } = "okhttp/3.12.1";

    public string ClientVersion { get; init; } = "a-6.3-1922";

    public string DeviceModel { get; init; } = "PC";

    public string Locale { get; init; } = "en_US";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Result of a WARP device registration. Token and private key must never be logged.</summary>
public sealed record WarpRegistration
{
    public required string AccountId { get; init; }

    public required string Token { get; init; }

    public required string LicenseKey { get; init; }

    /// <summary>Base64 WireGuard private key.</summary>
    public required string PrivateKey { get; init; }

    /// <summary>Base64 WireGuard public key sent to Cloudflare.</summary>
    public required string PublicKey { get; init; }

    public required string AddressV4 { get; init; }

    public string? AddressV6 { get; init; }

    public required string PeerPublicKey { get; init; }

    public required string EndpointHost { get; init; }

    public int EndpointPort { get; init; } = 2408;

    /// <summary>Optional 3-byte WARP client id for the WireGuard <c>reserved</c> field.</summary>
    public byte[]? Reserved { get; init; }

    public bool WarpEnabled { get; init; }
}

/// <summary>
/// Registers a WARP device with the unofficial Cloudflare client API
/// (same endpoints as wgcf, MIT; no wgcf code is used).
/// Call only from an explicit user action; tests use a fake <see cref="HttpClient"/> handler.
/// </summary>
public sealed class WarpClient(HttpClient http, LogRedactor redactor)
{
    /// <summary>Registers a device, enables WARP and fetches the WireGuard config.</summary>
    public async Task<WarpRegistration> RegisterAsync(WarpOptions? options = null, CancellationToken ct = default)
    {
        options ??= new WarpOptions();
        var privateKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var publicKey = X25519.GeneratePublicKey(privateKey);
            var privateB64 = Convert.ToBase64String(privateKey);
            var publicB64 = Convert.ToBase64String(publicKey);
            redactor.RegisterSecret(privateB64);
            redactor.RegisterSecret(publicB64);

            var regUrl = $"{options.ApiBase.TrimEnd('/')}/{options.ApiVersion}/reg";
            var tos = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var registerBody = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["install_id"] = "",
                ["tos"] = tos,
                ["key"] = publicB64,
                ["fcm_token"] = "",
                ["type"] = "Android",
                ["model"] = options.DeviceModel,
                ["locale"] = options.Locale,
            });

            using var regDoc = await PostJsonAsync(regUrl, registerBody, token: null, options, ct).ConfigureAwait(false);
            var accountId = Required(regDoc, "id");
            var token = Required(regDoc, "token");
            var license = regDoc.RootElement.TryGetProperty("account", out var account) && account.TryGetProperty("license", out var lic)
                ? lic.GetString() ?? ""
                : "";
            redactor.RegisterSecret(token);
            if (!string.IsNullOrEmpty(license))
            {
                redactor.RegisterSecret(license);
            }

            // Enable WARP on the new account (mirrors the official client flow).
            var configUrl = regUrl + "/" + accountId;
            using var patchDoc = await PatchJsonAsync(configUrl, """{"warp_enabled":true}""", token, options, ct).ConfigureAwait(false);
            var warpEnabled = patchDoc.RootElement.TryGetProperty("warp_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

            using var getDoc = await GetJsonAsync(configUrl, token, options, ct).ConfigureAwait(false);
            var root = getDoc.RootElement;
            if (root.TryGetProperty("warp_enabled", out var ge) && ge.ValueKind == JsonValueKind.True)
            {
                warpEnabled = true;
            }

            if (!root.TryGetProperty("config", out var config))
            {
                throw WarpError("The WARP server returned no WireGuard config.");
            }

            var addresses = config.TryGetProperty("interface", out var iface) && iface.TryGetProperty("addresses", out var addrs) ? addrs : default;
            var v4 = addresses.ValueKind == JsonValueKind.Object && addresses.TryGetProperty("v4", out var a4) ? a4.GetString() : null;
            var v6 = addresses.ValueKind == JsonValueKind.Object && addresses.TryGetProperty("v6", out var a6) ? a6.GetString() : null;
            if (string.IsNullOrWhiteSpace(v4))
            {
                throw WarpError("The WARP server returned no IPv4 address.");
            }

            if (!config.TryGetProperty("peers", out var peers) || peers.ValueKind != JsonValueKind.Array || peers.GetArrayLength() == 0)
            {
                throw WarpError("The WARP server returned no peers.");
            }

            var peer = peers[0];
            var peerKey = peer.TryGetProperty("public_key", out var pk) ? pk.GetString() : null;
            if (string.IsNullOrWhiteSpace(peerKey))
            {
                throw WarpError("The WARP server returned no peer key.");
            }

            var (host, port) = ParseEndpoint(peer.TryGetProperty("endpoint", out var ep) ? ep : default);
            return new WarpRegistration
            {
                AccountId = accountId,
                Token = token,
                LicenseKey = license,
                PrivateKey = privateB64,
                PublicKey = publicB64,
                AddressV4 = v4!,
                AddressV6 = string.IsNullOrWhiteSpace(v6) ? null : v6,
                PeerPublicKey = peerKey!,
                EndpointHost = host,
                EndpointPort = port,
                Reserved = ParseReserved(config),
                WarpEnabled = warpEnabled,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> factory, WarpOptions options, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout);
        using var request = factory();
        request.Headers.UserAgent.ParseAdd(options.UserAgent);
        request.Headers.TryAddWithoutValidation("CF-Client-Version", options.ClientVersion);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The body may echo credentials; report only the status.
            throw WarpError($"WARP registration failed (HTTP {(int)response.StatusCode}).");
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false), cancellationToken: cts.Token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw WarpError("The WARP server returned an unreadable reply.", ex);
        }
    }

    private Task<JsonDocument> PostJsonAsync(string url, string body, string? token, WarpOptions options, CancellationToken ct) =>
        SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (token is not null)
            {
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return m;
        }, options, ct);

    private Task<JsonDocument> PatchJsonAsync(string url, string body, string token, WarpOptions options, CancellationToken ct) =>
        SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }, options, ct);

    private Task<JsonDocument> GetJsonAsync(string url, string token, WarpOptions options, CancellationToken ct) =>
        SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Get, url);
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }, options, ct);

    private static string Required(JsonDocument doc, string name) =>
        doc.RootElement.TryGetProperty(name, out var v) && v.GetString() is { } s && s.Length > 0
            ? s
            : throw WarpError($"The WARP server reply has no '{name}'.");

    private static (string Host, int Port) ParseEndpoint(JsonElement endpoint)
    {
        string? raw = endpoint.ValueKind switch
        {
            JsonValueKind.String => endpoint.GetString(),
            JsonValueKind.Object when endpoint.TryGetProperty("host", out var h) => h.GetString(),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var text = raw.Trim();
            if (text.StartsWith('['))
            {
                var close = text.IndexOf(']');
                if (close > 1 && text.Length > close + 2 && text[close + 1] == ':'
                    && int.TryParse(text[(close + 2)..], out var p6) && p6 is > 0 and <= 65535)
                {
                    return (text[1..close], p6);
                }
            }
            else if (text.Count(c => c == ':') == 1)
            {
                // Exactly one colon: host:port. Bare IPv6 and other shapes keep the default port.
                var colon = text.IndexOf(':');
                if (colon > 0 && int.TryParse(text[(colon + 1)..], out var port) && port is > 0 and <= 65535)
                {
                    return (text[..colon], port);
                }

                return (text, 2408);
            }
            else if (!text.Contains(':'))
            {
                return (text, 2408);
            }
        }

        // Fallback to the well-known WARP entry point.
        return ("engage.cloudflareclient.com", 2408);
    }

    private static byte[]? ParseReserved(JsonElement config)
    {
        if (!config.TryGetProperty("client_id", out var id))
        {
            return null;
        }

        if (id.ValueKind == JsonValueKind.Array && id.GetArrayLength() == 3)
        {
            var bytes = new byte[3];
            for (var i = 0; i < 3; i++)
            {
                if (!id[i].TryGetByte(out bytes[i]))
                {
                    return null;
                }
            }

            return bytes;
        }

        if (id.ValueKind == JsonValueKind.String && id.GetString() is { } s && s.Length > 0)
        {
            // Base64-encoded 3 bytes, as returned for WARP client ids.
            try
            {
                var raw = Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
                if (raw.Length == 3)
                {
                    return raw;
                }
            }
            catch (FormatException)
            {
            }
        }

        return null;
    }

    private static Engines.EngineException WarpError(string message, Exception? inner = null) =>
        new(message, inner, localizationKey: "warp.error.register");
}
