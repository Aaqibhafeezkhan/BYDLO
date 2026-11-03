using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class DnsConfigTests
{
    private static readonly Profile Server = new() { Protocol = ProtocolKind.Trojan, Host = "server.invalid", Port = 443 };

    [Theory]
    [InlineData(DnsTransport.System, "local", null)]
    [InlineData(DnsTransport.Udp, "udp", "proxy")]
    [InlineData(DnsTransport.Tcp, "tcp", "proxy")]
    [InlineData(DnsTransport.Https, "https", "proxy")]
    [InlineData(DnsTransport.Tls, "tls", "proxy")]
    [InlineData(DnsTransport.Quic, "quic", "proxy")]
    public void Sing_box_builds_configured_dns_transports(DnsTransport transport, string type, string? detour)
    {
        var settings = new AppSettings { Routing = { RemoteDnsTransport = transport } };
        var dns = SingBox(settings);
        Assert.Equal(type, (string?)dns["servers"]![0]!["type"]);
        Assert.Equal(detour, (string?)dns["servers"]![0]!["detour"]);
    }

    [Theory]
    [InlineData(ConnectionMode.Tun, true)]
    [InlineData(ConnectionMode.LocalProxy, false)]
    public void Fake_ip_is_tun_only_and_respects_domain_exceptions(ConnectionMode mode, bool enabled)
    {
        var settings = new AppSettings { Mode = mode, Routing = { FakeIp = true, FakeIpExceptions = ["local.invalid"] } };
        var dns = SingBox(settings);
        Assert.Equal(enabled, dns["servers"]!.AsArray().Any(server => (string?)server?["type"] == "fakeip"));
        if (enabled)
        {
            Assert.Equal("local.invalid", (string?)dns["rules"]![0]!["domain_suffix"]![0]);
            Assert.Equal("fakeip", (string?)dns["rules"]![1]!["server"]);
        }
    }

    [Theory]
    [InlineData(DnsTransport.System, false, "localhost")]
    [InlineData(DnsTransport.Udp, true, "9.9.9.9")]
    [InlineData(DnsTransport.Tcp, true, "tcp://9.9.9.9")]
    [InlineData(DnsTransport.Https, false, "https://9.9.9.9/dns-query")]
    [InlineData(DnsTransport.Quic, false, "quic+local://9.9.9.9")]
    public void Xray_translates_supported_dns_transports(DnsTransport transport, bool throughProxy, string target)
    {
        var settings = new AppSettings
        {
            Routing = { RemoteDns = "9.9.9.9", RemoteDnsTransport = transport, RemoteDnsThroughProxy = throughProxy },
        };
        var json = new XrayConfigBuilder().Build(Server, ProfileSecrets.Empty, new BuildContext(settings));
        Assert.Equal(target, (string?)json["dns"]!["servers"]![0]!["address"]);
        if (throughProxy)
        {
            Assert.Equal("dns-remote", (string?)json["routing"]!["rules"]![0]!["inboundTag"]![0]);
            Assert.Equal("proxy", (string?)json["routing"]!["rules"]![0]!["outboundTag"]);
        }
    }

    [Theory]
    [InlineData(DnsTransport.Tls, false)]
    [InlineData(DnsTransport.Quic, true)]
    public void Xray_reports_unsupported_dns_combinations(DnsTransport transport, bool throughProxy)
    {
        var settings = new AppSettings { Routing = { RemoteDnsTransport = transport, RemoteDnsThroughProxy = throughProxy } };
        var error = Assert.Throws<EngineException>(() =>
            new XrayConfigBuilder().Build(Server, ProfileSecrets.Empty, new BuildContext(settings)));
        Assert.Equal("routing.dns.xray", error.LocalizationKey);
    }

    private static JsonNode SingBox(AppSettings settings) =>
        new SingBoxConfigBuilder().Build(Server, new ProfileSecrets { Values = { [Keys.Password] = "fake-password" } }, new BuildContext(settings))["dns"]!;
}
