using System.Text;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public class ExportTests
{
    public static TheoryData<string> Links => new()
    {
        "vless://id@[2001:db8::1]:8443?security=reality&pbk=public&sid=ab&spx=%2Fa%3Fb%3Dc&fp=chrome&sni=example.com&type=xhttp&mode=auto&path=%2Fa&host=cdn&flow=xtls-rprx-vision&alpn=h2&headerType=none&insecure=1#%D0%A2%D0%B5%D1%81%D1%82",
        "trojan://pa%3Ass%40%2F%23%25@example.com:443?security=tls&type=grpc&serviceName=a%2Fb&sni=example.com&allowInsecure=1#TR",
        "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pa:ss@/#%")) + "@example.com:8388?plugin=v2ray-plugin%3Btls%3Bhost%3Dcdn#SS",
        "hy2://auth%3Apass@example.com:443,20000-30000,40000/?sni=example.com&obfs=salamander&obfs-password=pa%26ss&alpn=h3&upmbps=10&downmbps=20&pinSHA256=abc&insecure=1#HY",
        "hy2://example.com:443#NoAuth",
        "tuic://uuid:pa%3Ass%40%23@example.com:443?congestion_control=bbr&udp_relay_mode=native&alpn=h3&sni=example.com&allow_insecure=1#T",
        "socks5://user:pa%40ss%3Aword@[2001:db8::1]:1080#S",
        "socks5://user@example.com:1080#NoPassword",
        "socks5://example.com:1080#Anonymous",
        "http://user:pass@example.com:8080#HTTP",
        "https://user:pass@example.com:443#HTTPS",
        Vmess("ws"),
        Vmess("grpc"),
    };

    private static string Vmess(string transport) => "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
        "{\"v\":\"2\",\"ps\":\"VM Тест\",\"add\":\"example.com\",\"port\":\"8443\",\"id\":\"uuid\",\"aid\":\"0\",\"scy\":\"auto\",\"net\":\"" + transport +
        "\",\"type\":\"none\",\"host\":\"cdn.example.com\",\"path\":\"/a?b=c\",\"tls\":\"tls\",\"sni\":\"example.com\",\"fp\":\"chrome\",\"alpn\":\"h2,http/1.1\"}"));

    [Theory]
    [MemberData(nameof(Links))]
    public void Import_export_import_preserves_connection(string link)
    {
        var first = One(link);
        var second = One(ProfileExporter.ExportLink(first.Profile, first.Secrets));
        SameConnection(first, second);
        Assert.Equal(first.Profile.Name, second.Profile.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WireGuard_export_preserves_all_peers_and_private_material(bool amnezia)
    {
        var config = "[Interface]\nPrivateKey = private\nAddress = 10.1.0.2/32\nDNS = 1.1.1.1\n" +
            (amnezia ? "Jc = 4\nS1 = 123\n" : "") +
            "[Peer]\nPublicKey = public1\nPresharedKey = preshared\nEndpoint = example.com:51820\nAllowedIPs = 0.0.0.0/0\n" +
            "[Peer]\nPublicKey = public2\nEndpoint = second.example.com:51820\nAllowedIPs = 10.2.0.0/16\n";
        var first = One(config);
        var exported = ProfileExporter.ExportConfig(first.Profile, first.Secrets);
        Assert.Equal(first.Secrets[Keys.RawConfig], exported);
        Assert.Equal(config.Trim(), exported);
        SameConnection(first, One(exported));
    }

    [Fact]
    public void OpenVpn_export_preserves_inline_certificates_and_options()
    {
        const string config = "client\ndev tun\nremote example.com 1194\nauth-user-pass\n<key>\nprivate material\n</key>\n<ca>\ncertificate\n</ca>\n";
        var first = One(config);
        var exported = ProfileExporter.ExportConfig(first.Profile, first.Secrets);
        Assert.Equal(first.Secrets[Keys.RawConfig], exported);
        Assert.Equal(config.Trim(), exported);
        SameConnection(first, One(exported));
    }

    [Fact]
    public void WireGuard_uri_can_be_exported_as_config()
    {
        var first = One("wg://private@example.com:51820?publickey=public&address=10.0.0.2%2F32&presharedkey=psk");
        var exported = ProfileExporter.ExportConfig(first.Profile, first.Secrets);
        Assert.Equal(first.Secrets[Keys.RawConfig], exported);
        // ImportText trims surrounding whitespace; the URI parser generates a trailing newline.
        first.Secrets.Values[Keys.RawConfig] = first.Secrets[Keys.RawConfig]!.Trim();
        SameConnection(first, One(exported));
    }

    [Fact]
    public void Missing_credentials_and_unsupported_formats_fail_without_payload()
    {
        var first = One("trojan://secret@example.com:443");
        var error = Assert.Throws<InvalidOperationException>(() => ProfileExporter.ExportLink(first.Profile, ProfileSecrets.Empty));
        Assert.DoesNotContain("example.com", error.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => ProfileExporter.ExportConfig(first.Profile, first.Secrets));
        Assert.Throws<NotSupportedException>(() => ProfileExporter.ExportLink(new Profile { Protocol = ProtocolKind.WireGuard }, ProfileSecrets.Empty));
        Assert.Throws<InvalidOperationException>(() => ProfileExporter.ExportConfig(new Profile { Protocol = ProtocolKind.OpenVpn }, ProfileSecrets.Empty));
    }

    private static ImportedProfile One(string text)
    {
        var result = new ProfileImporter().ImportText(text);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Profiles);
    }

    private static void SameConnection(ImportedProfile first, ImportedProfile second)
    {
        Assert.Equal(first.Profile.Protocol, second.Profile.Protocol);
        Assert.Equal(first.Profile.Host, second.Profile.Host);
        Assert.Equal(first.Profile.Port, second.Profile.Port);
        Assert.Equal(first.Profile.Options.OrderBy(k => k.Key), second.Profile.Options.OrderBy(k => k.Key));
        Assert.Equal(first.Secrets.Values.OrderBy(k => k.Key), second.Secrets.Values.OrderBy(k => k.Key));
    }
}
