using System.Text;
using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public class Issue261Tests
{
    private static ImportedProfile Import(string text)
    {
        var r = new ProfileImporter().ImportText(text);
        Assert.Empty(r.Errors);
        return Assert.Single(r.Profiles);
    }

    private static ImportResult TryImport(string text) => new ProfileImporter().ImportText(text);

    private static JsonObject SingBox(ImportedProfile p, AppSettings? s = null) =>
        new SingBoxConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(s ?? new AppSettings()));

    private static JsonObject Xray(ImportedProfile p, AppSettings? s = null) =>
        new XrayConfigBuilder().Build(p.Profile, p.Secrets, new BuildContext(s ?? new AppSettings()));

    private static EngineRegistry Registry() =>
        new EngineRegistry(() => new MockEngine())
            .Register(new EngineDescriptor { Kind = EngineKind.SingBox, DisplayName = "sb", Upstream = "", License = "", Protocols = SingBoxConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 10)
            .Register(new EngineDescriptor { Kind = EngineKind.Xray, DisplayName = "x", Upstream = "", License = "", Protocols = XrayConfigBuilder.Supported, Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented } }, () => new MockEngine(), 20);

    [Fact]
    public void Vless_encryption_is_preserved_to_xray_and_export()
    {
        var link = "vless://11111111-2222-3333-4444-555555555555@x.example.com:443?encryption=mlkem768x25519plus.native.0rtt.AAAA&security=reality&pbk=PBK&sid=ab&type=xhttp&path=%2Fx&mode=auto&sni=x.example.com#E";
        var p = Import(link);
        Assert.Equal("mlkem768x25519plus.native.0rtt.AAAA", p.Profile.Option(Keys.Encryption));

        var root = Xray(p);
        var user = root["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!;
        Assert.Equal("mlkem768x25519plus.native.0rtt.AAAA", (string?)user["encryption"]);

        Assert.Equal([EngineKind.Xray], Registry().CandidatesFor(p.Profile, OsFamily.Windows));

        var ex = Assert.Throws<EngineException>(() => SingBox(p));
        Assert.Equal("error.engine.transport.xray", ex.LocalizationKey);

        var exported = ProfileExporter.ExportLink(p.Profile, p.Secrets);
        Assert.Contains("encryption=mlkem768x25519plus.native.0rtt.AAAA", exported, StringComparison.Ordinal);
        var second = Import(exported);
        Assert.Equal(p.Profile.Option(Keys.Encryption), second.Profile.Option(Keys.Encryption));
    }

    [Fact]
    public void Vless_missing_encryption_defaults_to_none_and_round_trips()
    {
        var p = Import("vless://11111111-2222-3333-4444-555555555555@example.com:443?security=reality&sni=example.com&fp=chrome&pbk=PBK&sid=ab&type=tcp#N");
        Assert.Equal("none", p.Profile.Option(Keys.Encryption));
        var root = Xray(p);
        Assert.Equal("none", (string?)root["outbounds"]![0]!["settings"]!["vnext"]![0]!["users"]![0]!["encryption"]);
        var second = Import(ProfileExporter.ExportLink(p.Profile, p.Secrets));
        Assert.Equal("none", second.Profile.Option(Keys.Encryption));
    }

    [Fact]
    public void Xhttp_extra_is_preserved_and_validated()
    {
        var extra = Uri.EscapeDataString("{\"xPaddingBytes\":\"100-1000\",\"noGRPCHeader\":true}");
        var p = Import($"vless://id@x.example.com:443?security=tls&type=xhttp&path=%2Fx&mode=auto&sni=x.example.com&extra={extra}#X");
        Assert.Contains("xPaddingBytes", p.Profile.Option(Keys.XhttpExtra)!, StringComparison.Ordinal);

        var root = Xray(p);
        var xhttp = root["outbounds"]![0]!["streamSettings"]!["xhttpSettings"]!;
        Assert.Equal("/x", (string?)xhttp["path"]);
        Assert.Equal("100-1000", (string?)xhttp["extra"]!["xPaddingBytes"]);
        Assert.True((bool)xhttp["extra"]!["noGRPCHeader"]!);

        var second = Import(ProfileExporter.ExportLink(p.Profile, p.Secrets));
        Assert.Equal(p.Profile.Option(Keys.XhttpExtra), second.Profile.Option(Keys.XhttpExtra));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[1,2]")]
    [InlineData("%7B%22downloadSettings%22%3A%7B%7D%7D")]
    [InlineData("%7B%22host%22%3A%22evil%22%7D")]
    [InlineData("%7B%22xPaddingBytes%22%3A%22ext%3Afoo%22%7D")]
    [InlineData("%7B%22xPaddingBytes%22%3A%22file%3Afoo%22%7D")]
    [InlineData("%7B%22xPaddingBytes%22%3A%22ext-ip%3Afoo%22%7D")]
    [InlineData("%7B%22noGRPCHeader%22%3A%7B%22evil%22%3A1%7D%7D")]
    public void Xhttp_extra_invalid_is_rejected_without_echoing_credentials(string extraParam)
    {
        var r = TryImport($"vless://supersecretuuid@x.example.com:443?security=tls&type=xhttp&extra={extraParam}#X");
        Assert.Empty(r.Profiles);
        var error = Assert.Single(r.Errors);
        Assert.DoesNotContain("supersecretuuid", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Xhttp_extra_too_large_is_rejected()
    {
        var big = new string('a', 9000);
        var r = TryImport($"vless://supersecretuuid@x.example.com:443?security=tls&type=xhttp&extra={Uri.EscapeDataString("{\"xPaddingBytes\":\"" + big + "\"}")}#X");
        Assert.Empty(r.Profiles);
    }

    [Fact]
    public void Tcp_http_header_goes_to_xray_with_raw_settings()
    {
        var p = Import("vless://id@x.example.com:443?security=tls&type=tcp&headerType=http&host=a.example&path=%2F&sni=x.example.com#H");
        Assert.Equal([EngineKind.Xray], Registry().CandidatesFor(p.Profile, OsFamily.Windows));

        var root = Xray(p);
        var stream = root["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("raw", (string?)stream["network"]);
        Assert.Equal("http", (string?)stream["tcpSettings"]!["header"]!["type"]);

        var ex = Assert.Throws<EngineException>(() => SingBox(p));
        Assert.Equal("error.engine.transport.xray", ex.LocalizationKey);

        var second = Import(ProfileExporter.ExportLink(p.Profile, p.Secrets));
        Assert.Equal("http", second.Profile.Option(Keys.HeaderType));
    }

    [Fact]
    public void Vmess_tcp_http_header_goes_to_xray()
    {
        var json = """{"v":"2","ps":"VM","add":"vm.example.com","port":"8443","id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","aid":"0","scy":"auto","net":"tcp","type":"http","host":"a.example","path":"/","tls":"none"}""";
        var p = Import("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
        Assert.Equal([EngineKind.Xray], Registry().CandidatesFor(p.Profile, OsFamily.Windows));
        var stream = Xray(p)["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("http", (string?)stream["tcpSettings"]!["header"]!["type"]);
        var ex = Assert.Throws<EngineException>(() => SingBox(p));
        Assert.Equal("error.engine.transport.xray", ex.LocalizationKey);
    }

    [Fact]
    public void Trojan_does_not_store_vless_encryption()
    {
        var p = Import("trojan://secret@tr.example.com:443?security=tls&type=tcp&sni=tr.example.com&encryption=foo#T");
        Assert.Null(p.Profile.Option(Keys.Encryption));
        Assert.Contains(EngineKind.SingBox, Registry().CandidatesFor(p.Profile, OsFamily.Windows));
        SingBox(p);
        Xray(p);
    }

    [Fact]
    public void Plain_tcp_has_no_fake_header_and_builds_on_both_engines()
    {
        var p = Import("vless://id@x.example.com:443?security=tls&type=tcp&sni=x.example.com#P");
        var xrayStream = Xray(p)["outbounds"]![0]!["streamSettings"]!;
        Assert.Null(xrayStream["tcpSettings"]);
        SingBox(p);
    }

    [Theory]
    [InlineData("kcp")]
    [InlineData("mkcp")]
    public void Kcp_is_routed_to_xray_with_kcp_settings(string transport)
    {
        var p = Import($"vless://id@x.example.com:443?security=tls&type={transport}&headerType=utp&seed=xxx&sni=x.example.com#K");
        Assert.Equal([EngineKind.Xray], Registry().CandidatesFor(p.Profile, OsFamily.Windows));
        var stream = Xray(p)["outbounds"]![0]!["streamSettings"]!;
        Assert.Equal("kcp", (string?)stream["network"]);
        Assert.Equal("utp", (string?)stream["kcpSettings"]!["header"]!["type"]);
        Assert.Equal("xxx", (string?)stream["kcpSettings"]!["seed"]);
        var ex = Assert.Throws<EngineException>(() => SingBox(p));
        Assert.Equal("error.engine.transport.xray", ex.LocalizationKey);
        var second = Import(ProfileExporter.ExportLink(p.Profile, p.Secrets));
        Assert.Equal("xxx", second.Profile.Option(Keys.Seed));
    }

    [Fact]
    public void Shadowsocks_plugin_goes_to_singbox_and_xray_rejects_it()
    {
        var userinfo = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-256-gcm:pass")).TrimEnd('=');
        var p = Import($"ss://{userinfo}@ss.example.com:8388?plugin=v2ray-plugin%3Btls%3Bhost%3Dcdn#SS");
        Assert.Equal([EngineKind.SingBox], Registry().CandidatesFor(p.Profile, OsFamily.Windows));
        var outbound = SingBox(p)["outbounds"]![0]!;
        Assert.Equal("v2ray-plugin", (string?)outbound["plugin"]);
        var ex = Assert.Throws<EngineException>(() => Xray(p));
        Assert.Equal("error.engine.transport.singbox", ex.LocalizationKey);
    }

    [Fact]
    public async Task Cancelled_import_throws_on_cancelled_token()
    {
        var dir = TestDirs.New("261-cancel");
        var path = Path.Combine(dir, "sub.txt");
        await File.WriteAllTextAsync(path, "vless://id@x.example.com:443?security=tls#X");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ProfileImporter().ImportFileAsync(path, cts.Token));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Export_import_preserves_all_transport_options_without_duplicates()
    {
        var extra = Uri.EscapeDataString("{\"xPaddingBytes\":\"100-1000\",\"noGRPCHeader\":true}");
        var link = $"vless://11111111-2222-3333-4444-555555555555@x.example.com:443?encryption=mlkem768x25519plus.native.0rtt.AAAA&security=tls&type=xhttp&path=%2Fx&host=cdn&mode=auto&headerType=none&seed=s&extra={extra}&sni=x.example.com#T";
        var first = Import(link);
        var second = Import(ProfileExporter.ExportLink(first.Profile, first.Secrets));
        Assert.Equal(first.Profile.Options.OrderBy(k => k.Key), second.Profile.Options.OrderBy(k => k.Key));
        Assert.Equal(first.Secrets.Values.OrderBy(k => k.Key), second.Secrets.Values.OrderBy(k => k.Key));
    }

    [Fact]
    public void Localization_covers_new_transport_errors()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));
        Assert.Contains("error.engine.transport.xray", en, StringComparison.Ordinal);
        Assert.Contains("error.engine.transport.xray", ru, StringComparison.Ordinal);
        Assert.Contains("error.engine.transport.singbox", en, StringComparison.Ordinal);
        Assert.Contains("error.engine.transport.singbox", ru, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
