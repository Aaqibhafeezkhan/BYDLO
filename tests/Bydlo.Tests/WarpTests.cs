using System.Net;
using System.Text;
using Bydlo.Core.Engines;
using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Warp;

namespace Bydlo.Tests;

/// <summary>WARP registration (issue #85) unit tests with a fake HTTP handler. No real network.</summary>
public class WarpTests
{
    private const string AlicePrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
    private const string AlicePublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
    private const string BobPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
    private const string BobPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
    private const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

    private static byte[] Hex(string s) => Convert.FromHexString(s);

    [Fact]
    public void X25519_matches_rfc7748_vectors()
    {
        Assert.Equal(AlicePublic, Convert.ToHexString(X25519.GeneratePublicKey(Hex(AlicePrivate))).ToLowerInvariant());
        Assert.Equal(BobPublic, Convert.ToHexString(X25519.GeneratePublicKey(Hex(BobPrivate))).ToLowerInvariant());
        Assert.Equal(Shared, Convert.ToHexString(X25519.ScalarMult(Hex(AlicePrivate), Hex(BobPublic))).ToLowerInvariant());
        Assert.Equal(Shared, Convert.ToHexString(X25519.ScalarMult(Hex(BobPrivate), Hex(AlicePublic))).ToLowerInvariant());
    }

    [Fact]
    public void X25519_random_pairs_agree()
    {
        var a = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var b = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Assert.Equal(
            X25519.ScalarMult(a, X25519.GeneratePublicKey(b)),
            X25519.ScalarMult(b, X25519.GeneratePublicKey(a)));
    }

    private sealed class FakeWarpHandler : HttpMessageHandler
    {
        public List<(string Method, string Url)> Calls { get; } = [];

        public List<string> PostBodies { get; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.Method.Method, request.RequestUri!.ToString()));
            string payload = request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/reg") && request.Method == HttpMethod.Post =>
                    """{"id":"device-1","token":"token-abc","account":{"license":"lic-123"}}""",
                var p when p.EndsWith("/reg/device-1") && request.Method == HttpMethod.Patch =>
                    """{"warp_enabled":true}""",
                var p when p.EndsWith("/reg/device-1") && request.Method == HttpMethod.Get =>
                    """{"warp_enabled":true,"config":{"interface":{"addresses":{"v4":"172.16.0.2","v6":"2606:4700:110:abcd::2"}},"peers":[{"endpoint":"engage.cloudflareclient.com:2408","public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}],"client_id":[1,2,3]}}""",
                _ => """{}""",
            };
            if (request.Content is not null)
            {
                PostBodies.Add(await request.Content.ReadAsStringAsync(ct));
            }

            return new HttpResponseMessage(Status) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        }
    }

    private static WarpClient Client(HttpMessageHandler handler, out LogRedactor redactor)
    {
        redactor = new LogRedactor();
        return new WarpClient(new HttpClient(handler), redactor);
    }

    [Fact]
    public async Task Register_posts_key_enables_warp_and_reads_config()
    {
        var handler = new FakeWarpHandler();
        var client = Client(handler, out _);
        var reg = await client.RegisterAsync(new WarpOptions { ApiBase = "https://warp.example.invalid", ApiVersion = "v0a1" });

        Assert.Equal(["POST", "PATCH", "GET"], handler.Calls.Select(c => c.Method).ToList());
        Assert.All(handler.Calls, c => Assert.StartsWith("https://warp.example.invalid/v0a1/reg", c.Url));
        Assert.Contains(handler.PostBodies, b => b.Contains("\"tos\"") && b.Contains("\"key\"") && b.Contains("\"type\":\"Android\""));
        Assert.Equal("device-1", reg.AccountId);
        Assert.Equal("token-abc", reg.Token);
        Assert.Equal("lic-123", reg.LicenseKey);
        Assert.Equal("172.16.0.2", reg.AddressV4);
        Assert.Equal("2606:4700:110:abcd::2", reg.AddressV6);
        Assert.Equal("engage.cloudflareclient.com", reg.EndpointHost);
        Assert.Equal(2408, reg.EndpointPort);
        Assert.Equal([1, 2, 3], reg.Reserved);
        Assert.True(reg.WarpEnabled);
        Assert.Equal(32, Convert.FromBase64String(reg.PrivateKey).Length);
        Assert.Equal(32, Convert.FromBase64String(reg.PublicKey).Length);
    }

    [Fact]
    public async Task Register_failure_reports_status_without_body()
    {
        var handler = new FakeWarpHandler { Status = HttpStatusCode.Forbidden };
        var client = Client(handler, out _);
        var ex = await Assert.ThrowsAsync<EngineException>(() => client.RegisterAsync());
        Assert.Equal("warp.error.register", ex.LocalizationKey);
        Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task Register_redacts_token_and_private_key()
    {
        var handler = new FakeWarpHandler();
        var client = Client(handler, out var redactor);
        var reg = await client.RegisterAsync();
        Assert.DoesNotContain(reg.Token, redactor.Redact($"token {reg.Token}"), StringComparison.Ordinal);
        Assert.DoesNotContain(reg.PrivateKey, redactor.Redact($"key {reg.PrivateKey}"), StringComparison.Ordinal);
    }

    private sealed class VariantHandler : HttpMessageHandler
    {
        private readonly string _getPayload;

        public VariantHandler(string getPayload) => _getPayload = getPayload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string payload = request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith("/reg") && request.Method == HttpMethod.Post =>
                    """{"id":"device-9","token":"tok-9","account":{"license":""}}""",
                var p when p.EndsWith("/reg/device-9") && request.Method == HttpMethod.Patch =>
                    """{"warp_enabled":true}""",
                _ => _getPayload,
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Register_reads_object_endpoint_and_base64_reserved()
    {
        var handler = new VariantHandler(
            """{"config":{"interface":{"addresses":{"v4":"172.16.0.9"}},"peers":[{"endpoint":{"host":"162.159.192.1"},"public_key":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}],"client_id":"AQID"}}""");
        var client = Client(handler, out _);
        var reg = await client.RegisterAsync();
        Assert.Equal("162.159.192.1", reg.EndpointHost);
        Assert.Equal(2408, reg.EndpointPort);
        Assert.Equal([1, 2, 3], reg.Reserved);
        Assert.Null(reg.AddressV6);
    }

    [Fact]
    public async Task Register_rejects_reply_without_config()
    {
        var handler = new VariantHandler("""{"warp_enabled":true}""");
        var client = Client(handler, out _);
        var ex = await Assert.ThrowsAsync<EngineException>(() => client.RegisterAsync());
        Assert.Equal("warp.error.register", ex.LocalizationKey);
    }

    [Fact]
    public void Factory_builds_wireguard_profile_with_warp_secrets()
    {
        var reg = new WarpRegistration
        {
            AccountId = "device-1",
            Token = "token-abc",
            LicenseKey = "lic-123",
            PrivateKey = Convert.ToBase64String(new byte[32]),
            PublicKey = Convert.ToBase64String(new byte[32]),
            AddressV4 = "172.16.0.2",
            AddressV6 = "2606:4700:110:abcd::2",
            PeerPublicKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
            EndpointHost = "engage.cloudflareclient.com",
            EndpointPort = 2408,
            Reserved = [9, 9, 9],
            WarpEnabled = true,
        };
        var imported = WarpProfileFactory.Create("WARP", reg);
        Assert.Equal("WARP", imported.Profile.Name);
        Assert.Equal(ProtocolKind.WireGuard, imported.Profile.Protocol);
        Assert.Equal("engage.cloudflareclient.com", imported.Profile.Host);
        Assert.Equal(2408, imported.Profile.Port);
        Assert.Equal("9,9,9", imported.Profile.Option(Keys.Reserved));
        Assert.Equal("device-1", imported.Secrets[Keys.WarpAccountId]);
        Assert.Equal("token-abc", imported.Secrets[Keys.WarpToken]);
        Assert.Equal("lic-123", imported.Secrets[Keys.WarpLicense]);

        // The stored config round-trips through the regular WireGuard parser.
        var reparsed = new WireGuardConfParser().Parse(imported.Secrets[Keys.RawConfig]!);
        Assert.Equal(ProtocolKind.WireGuard, reparsed.Profile.Protocol);
    }

    [Fact]
    public void Factory_rejects_empty_name()
    {
        var handler = new FakeWarpHandler();
        var client = Client(handler, out _);
        Assert.Throws<ArgumentException>(() => WarpProfileFactory.Create("  ", new WarpRegistration
        {
            AccountId = "x", Token = "y", LicenseKey = "", PrivateKey = "eA==", PublicKey = "eA==",
            AddressV4 = "172.16.0.2", PeerPublicKey = "eA==", EndpointHost = "engage.cloudflareclient.com",
        }));
    }
}
