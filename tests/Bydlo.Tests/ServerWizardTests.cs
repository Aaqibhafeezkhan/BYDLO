using System.Net;
using System.Text;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class ServerWizardTests
{
    [Theory]
    [InlineData("https://subscription.example", true)]
    [InlineData("https://subscription.example/token", true)]
    [InlineData("https://user:password@proxy.example:8080/path", false)]
    [InlineData("http://proxy.example:8080", false)]
    [InlineData("vless://id@server.example:443", false)]
    public void Wizard_detects_root_subscription_URLs_without_confusing_authenticated_proxy_links(string text, bool expected) =>
        Assert.Equal(expected, ProfileImporter.LooksLikeSubscriptionUrl(text, allowRoot: true));

    [Theory]
    [InlineData(ProtocolKind.Vless)]
    [InlineData(ProtocolKind.Vmess)]
    [InlineData(ProtocolKind.Trojan)]
    [InlineData(ProtocolKind.Shadowsocks)]
    [InlineData(ProtocolKind.Hysteria2)]
    [InlineData(ProtocolKind.Tuic)]
    [InlineData(ProtocolKind.Socks5)]
    [InlineData(ProtocolKind.Http)]
    public void Manual_editor_keeps_credentials_in_secrets(ProtocolKind protocol)
    {
        var draft = new ManualProfileDraft
        {
            Protocol = protocol, Host = "[2001:db8::1]", Port = 8443, Name = "Test server",
            Identifier = "11111111-2222-3333-4444-555555555555", Password = "private-password", Sni = "tls.example",
        };
        var item = draft.Build();
        Assert.Equal("2001:db8::1", item.Profile.Host);
        Assert.Equal(protocol, item.Profile.Protocol);
        Assert.Equal("Test server", item.Profile.Name);
        Assert.Equal("private-password", item.Secrets[Keys.Password]);
        Assert.DoesNotContain("private-password", item.Profile.Options.Values);
        if (protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic)
        { Assert.Equal(draft.Identifier, item.Secrets[Keys.Uuid]); }
        if (protocol is ProtocolKind.Socks5 or ProtocolKind.Http)
        { Assert.Equal(draft.Identifier, item.Profile.Option(Keys.Username)); }
    }

    [Theory]
    [InlineData("https://server.example/private?token=secret", 443)]
    [InlineData("bad address", 443)]
    [InlineData("server.example", 0)]
    [InlineData("server.example", 65536)]
    public void Manual_editor_rejects_invalid_endpoints_without_echoing_input(string host, int port)
    {
        var draft = new ManualProfileDraft { Protocol = ProtocolKind.Trojan, Host = host, Port = port, Password = "private-password" };
        var error = Assert.Throws<FormatException>(() => draft.Build());
        Assert.DoesNotContain("private-password", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Manual_editor_validates_UUID_password_and_config_protocol()
    {
        Assert.Throws<FormatException>(() => new ManualProfileDraft { Host = "server.example", Identifier = "not-a-uuid" }.Build());
        Assert.Throws<FormatException>(() => new ManualProfileDraft { Protocol = ProtocolKind.Trojan, Host = "server.example" }.Build());
        var draft = new ManualProfileDraft { Protocol = ProtocolKind.OpenVpn, Name = "My configuration", Configuration = "client\ndev tun\nremote server.example 1194\n<key>\nprivate-key\n</key>" };
        var item = draft.Build();
        Assert.Equal("My configuration", item.Profile.Name);
        Assert.Contains("private-key", item.Secrets[Keys.RawConfig], StringComparison.Ordinal);
        draft.Protocol = ProtocolKind.WireGuard;
        Assert.Throws<FormatException>(() => draft.Build());
    }

    [Theory]
    [InlineData("upload=10; download=20; total=100; expire=2000000000", 10L, 20L, 100L, true)]
    [InlineData("upload=-1; download=no; total=999999999999999999999; expire=9999999999999", null, null, null, false)]
    [InlineData("UPLOAD=0; download=20; unused=x", 0L, 20L, null, false)]
    public void Subscription_metadata_is_optional_and_validated(string header, long? up, long? down, long? total, bool expires)
    {
        var metadata = SubscriptionMetadata.Parse(header);
        Assert.Equal(up, metadata.Upload);
        Assert.Equal(down, metadata.Download);
        Assert.Equal(total, metadata.Total);
        Assert.Equal(expires, metadata.Expires.HasValue);
    }

    [Fact]
    public async Task Subscription_preview_reads_headers_and_base64_without_persisting()
    {
        using var http = new HttpClient(new PreviewHandler());
        var download = await new SubscriptionClient(http, new()).FetchPreviewAsync("https://sub.example/secret-token");
        var item = Assert.Single(download.Import.Profiles);
        Assert.Equal("private-password", item.Secrets[Keys.Password]);
        Assert.Equal(100, download.Metadata.Total);
        Assert.Equal(30, download.Metadata.Download);
    }

    [Fact]
    public async Task Confirming_subscription_persists_the_previewed_profiles_and_protects_the_URL()
    {
        var folder = TestDirs.New("wizard-subscription");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://private-password@server.example:443#Test");
        Assert.Empty(store.Profiles);
        var sub = await store.AddSubscriptionWithProfilesAsync("Subscription", "https://sub.example/private-token", imported.Profiles);
        await store.LoadAsync();
        Assert.Equal(sub.Id, Assert.Single(store.Profiles).SubscriptionId);
        Assert.Equal("https://sub.example/private-token", await store.GetSubscriptionUrlAsync(sub.Id));
        var json = await File.ReadAllTextAsync(Path.Combine(folder, "profiles.json"));
        Assert.DoesNotContain("private-token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_subscription_save_leaves_no_subscription_profiles_or_credentials()
    {
        var folder = TestDirs.New("wizard-subscription");
        var path = Path.Combine(folder, "profiles.json");
        var store = new ProfileStore(path, new InMemorySecretStore());
        Directory.CreateDirectory(path + ".tmp");
        var imported = new ProfileImporter().ImportText("trojan://private-password@server.example:443#Test");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AddSubscriptionWithProfilesAsync("Subscription", "https://sub.example/private-token", imported.Profiles));
        Assert.Empty(store.Profiles);
        Assert.Empty(store.Subscriptions);
        Assert.Empty((await store.Secrets.LoadAsync(imported.Profiles[0].Profile.Id.ToString())).Values);
    }

    [Fact]
    public async Task Failed_profile_save_leaves_no_partial_profiles_or_credentials()
    {
        var folder = TestDirs.New("wizard-profiles");
        var path = Path.Combine(folder, "profiles.json");
        var store = new ProfileStore(path, new InMemorySecretStore());
        Directory.CreateDirectory(path + ".tmp");
        var imported = new ProfileImporter().ImportText("trojan://private-password@server.example:443#Test");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.AddAsync(imported.Profiles));
        Assert.Empty(store.Profiles);
        Assert.Empty((await store.Secrets.LoadAsync(imported.Profiles[0].Profile.Id.ToString())).Values);
    }

    private sealed class PreviewHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Convert.ToBase64String(Encoding.UTF8.GetBytes("trojan://private-password@server.example:443#Test"))),
            };
            response.Headers.TryAddWithoutValidation("subscription-userinfo", "upload=10; download=30; total=100; expire=2000000000");
            return Task.FromResult(response);
        }
    }
}
