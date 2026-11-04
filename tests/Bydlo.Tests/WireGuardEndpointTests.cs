using Bydlo.Core.Import;

namespace Bydlo.Tests;

/// <summary>Issue #221: malformed WireGuard Endpoint values must be reported, never thrown.</summary>
public class WireGuardEndpointTests
{
    private const string ConfTemplate = """
        [Interface]
        PrivateKey = yAnz5TF+lXXJte14tji3zlMNq+hd2rYUIgJBgB3fBmk=
        Address = 10.8.0.2/32

        [Peer]
        PublicKey = xTIBA5rboUvnH4htodjb6e697QjLERt1NAB4mZqp8Dg=
        AllowedIPs = 0.0.0.0/0
        Endpoint = {0}
        """;

    private readonly ProfileImporter _importer = new();

    [Theory]
    [InlineData("[2001:db8::1]")]
    [InlineData("[")]
    [InlineData("[2001:db8::1")]
    [InlineData("[]:51820")]
    [InlineData("[example.com]")]
    [InlineData("[example.com]:0")]
    [InlineData("[example.com]:99999")]
    [InlineData("[example.com]:notaport")]
    [InlineData("example.com")]
    public void Malformed_endpoint_is_reported_not_thrown(string endpoint)
    {
        var result = _importer.ImportText(ConfTemplate.Replace("{0}", endpoint, StringComparison.Ordinal));
        Assert.Empty(result.Profiles);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("[2001:db8::1]:51820", "2001:db8::1", 51820)]
    [InlineData("[fe80::1]:53", "fe80::1", 53)]
    [InlineData("wg.example.com:51820", "wg.example.com", 51820)]
    [InlineData("10.0.0.1:53", "10.0.0.1", 53)]
    public void WellFormed_endpoint_still_parses(string endpoint, string host, int port)
    {
        var result = _importer.ImportText(ConfTemplate.Replace("{0}", endpoint, StringComparison.Ordinal));
        Assert.Empty(result.Errors);
        var profile = Assert.Single(result.Profiles);
        Assert.Equal(host, profile.Profile.Host);
        Assert.Equal(port, profile.Profile.Port);
    }

    [Fact]
    public void Bad_link_does_not_drop_rest_of_batch()
    {
        const string good = "vless://11111111-2222-3333-4444-555555555555@example.com:443?encryption=none&security=reality&sni=www.microsoft.com&fp=chrome&pbk=PUBKEY123&sid=ab12&type=tcp&flow=xtls-rprx-vision#Good";
        var result = _importer.ImportText("vless://id@example.com:443?security=reality&sni=a.com\n" + good);
        var profile = Assert.Single(result.Profiles);
        Assert.Equal("Good", profile.Profile.Name);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Cancelled_file_import_throws_cancel_not_import_error()
    {
        var dir = TestDirs.New("wg-endpoint-cancel");
        var path = Path.Combine(dir, "bad.conf");
        await File.WriteAllTextAsync(path, ConfTemplate.Replace("{0}", "[", StringComparison.Ordinal));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _importer.ImportFileAsync(path, cts.Token));
    }
}
