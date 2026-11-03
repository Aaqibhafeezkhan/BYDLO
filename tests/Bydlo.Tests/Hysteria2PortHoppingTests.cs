using Bydlo.Core.Config;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class Hysteria2PortHoppingTests
{
    [Theory]
    [InlineData("hy2://pw@203.0.113.5:443,8443", "203.0.113.5", "pw", "443:443,8443:8443")]
    [InlineData("hysteria2://host.invalid:443,20000-30000?sni=tls.invalid#Test", "host.invalid", null, "443:443,20000:30000")]
    [InlineData("hy2://[2001:db8::1]:443,8443/", "2001:db8::1", null, "443:443,8443:8443")]
    [InlineData("hy2://user%3Apass@[2001:db8::1]:443,8443", "2001:db8::1", "user:pass", "443:443,8443:8443")]
    [InlineData("hy2://@host.invalid:20000-30000,443", "host.invalid", null, "20000:30000,443:443")]
    [InlineData("hy2://host.invalid:1,65535", "host.invalid", null, "1:1,65535:65535")]
    public void Links_preserve_every_port_and_round_trip(string link, string host, string? password, string expected)
    {
        var imported = Import(link);
        Assert.Equal(host, imported.Profile.Host);
        Assert.Equal(password, imported.Secrets[Keys.Password]);
        AssertPorts(imported, expected);
        var exported = ProfileExporter.ExportLink(imported.Profile, imported.Secrets);
        AssertPorts(Import(exported), expected);
    }

    [Theory]
    [InlineData("443,8443", "443:443,8443:8443")]
    [InlineData("20000-30000,8443", "20000:30000,8443:8443")]
    [InlineData("20000-30000", "20000:30000")]
    public void Clash_ports_override_the_single_port(string ports, string expected)
    {
        var imported = Import("proxies: [{type: hy2, server: host.invalid, port: 1234, password: pw, ports: '" + ports + "'}]");
        AssertPorts(imported, expected);
        AssertPorts(Import(ProfileExporter.ExportLink(imported.Profile, imported.Secrets)), expected);
    }

    [Theory]
    [InlineData("443,0")]
    [InlineData("443,65536")]
    [InlineData("443,30000-20000")]
    [InlineData("443,8443garbage")]
    [InlineData("443,,8443")]
    [InlineData("443,8443-")]
    [InlineData("0,443")]
    [InlineData("65536,443")]
    public void Invalid_lists_are_rejected_in_links_and_yaml(string ports)
    {
        foreach (var input in new[] { "hy2://host.invalid:" + ports,
            "proxies: [{type: hy2, server: host.invalid, port: 443, password: pw, ports: '" + ports + "'}]" })
        {
            var result = new ProfileImporter().ImportText(input);
            Assert.Empty(result.Profiles);
            Assert.Single(result.Errors);
        }
    }

    [Fact]
    public void Port_like_query_and_fragment_do_not_change_the_authority()
    {
        var imported = Import("hy2://host.invalid:443,8443/?sni=tls.invalid#label@other.invalid:1,2");
        Assert.Equal("tls.invalid", imported.Profile.Option(Keys.Sni));
        Assert.Equal("label@other.invalid:1,2", imported.Profile.Name);
        AssertPorts(imported, "443:443,8443:8443");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("''")]
    public void Clash_rejects_invalid_ports_nodes(string ports)
    {
        var result = new ProfileImporter().ImportText(
            "proxies: [{type: hy2, server: host.invalid, port: 443, password: pw, ports: " + ports + "}]");
        Assert.Empty(result.Profiles);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void Saved_profiles_with_additional_ports_keep_the_primary_port()
    {
        var profile = new Profile { Protocol = ProtocolKind.Hysteria2, Host = "host.invalid", Port = 443 };
        profile.Options["ports"] = "8443,20000-30000";
        AssertPorts(new ImportedProfile(profile, new ProfileSecrets()), "443:443,8443:8443,20000:30000");
    }

    [Fact]
    public void Single_port_does_not_enable_hopping()
    {
        var imported = Import("hy2://host.invalid:8443");
        var outbound = new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets,
            new BuildContext(new AppSettings()))["outbounds"]![0]!;
        Assert.Equal(8443, (int?)outbound["server_port"]);
        Assert.Null(outbound["server_ports"]);
    }

    private static ImportedProfile Import(string input)
    {
        var result = new ProfileImporter().ImportText(input);
        Assert.Empty(result.Errors);
        return Assert.Single(result.Profiles);
    }

    private static void AssertPorts(ImportedProfile imported, string expected)
    {
        var outbound = new SingBoxConfigBuilder().Build(imported.Profile, imported.Secrets,
            new BuildContext(new AppSettings()))["outbounds"]![0]!;
        Assert.Null(outbound["server_port"]);
        Assert.Equal(expected.Split(','), outbound["server_ports"]!.AsArray().Select(value => (string?)value));
    }
}
