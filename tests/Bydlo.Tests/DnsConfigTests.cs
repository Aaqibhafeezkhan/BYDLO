using System.Net;
using System.Text.Json.Nodes;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Xray;

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
    [InlineData(DnsTransport.System, "local")]
    [InlineData(DnsTransport.Udp, "udp")]
    [InlineData(DnsTransport.Tcp, "tcp")]
    [InlineData(DnsTransport.Https, "https")]
    [InlineData(DnsTransport.Tls, "tls")]
    [InlineData(DnsTransport.Quic, "quic")]
    public void Sing_box_applies_both_dns_directions(DnsTransport transport, string type)
    {
        foreach (var throughProxy in new[] { false, true })
        {
            var settings = new AppSettings { Routing =
            {
                RemoteDns = "9.9.9.9", DirectDns = "8.8.8.8",
                RemoteDnsTransport = transport, DirectDnsTransport = transport,
                RemoteDnsThroughProxy = throughProxy, DirectDnsThroughProxy = throughProxy,
            } };
            var servers = SingBox(settings)["servers"]!;
            foreach (var index in new[] { 0, 1 })
            {
                Assert.Equal(type, (string?)servers[index]!["type"]);
                Assert.Equal(throughProxy && transport != DnsTransport.System ? "proxy" : null,
                    (string?)servers[index]!["detour"]);
            }
        }
    }

    [Fact]
    public void Fake_ip_is_disabled_by_default_even_in_tun()
    {
        var settings = new AppSettings { Mode = ConnectionMode.Tun };
        Assert.False(settings.Routing.FakeIp);
        Assert.DoesNotContain(SingBox(settings)["servers"]!.AsArray(), server => (string?)server?["type"] == "fakeip");
    }

    [Fact]
    public void Explicit_dns_rules_precede_fake_ip_and_only_address_queries_use_fake_ip()
    {
        var settings = new AppSettings { Mode = ConnectionMode.Tun, Routing =
        {
            FakeIp = true, RemoteDnsDomains = ["remote.invalid"], DirectDnsDomains = ["direct.invalid"],
            FakeIpExceptions = ["exception.invalid"],
        } };
        var rules = SingBox(settings)["rules"]!;
        Assert.Equal("remote", (string?)rules[0]!["server"]);
        Assert.Equal("local", (string?)rules[1]!["server"]);
        Assert.Equal("exception.invalid", (string?)rules[2]!["domain_suffix"]![0]);
        Assert.Equal("fakeip", (string?)rules[3]!["server"]);
        Assert.Equal(new[] { "A", "AAAA" }, rules[3]!["query_type"]!.AsArray().Select(node => (string?)node));
    }

    [Fact]
    public void Fake_ip_ranges_do_not_overlap_private_networks()
    {
        var settings = new AppSettings { Mode = ConnectionMode.Tun, Routing = { FakeIp = true } };
        var fakeIp = SingBox(settings)["servers"]!.AsArray().Single(server => (string?)server?["type"] == "fakeip")!;
        Assert.Equal("198.18.0.0/15", (string?)fakeIp["inet4_range"]);
        var inet6 = IPNetwork.Parse((string)fakeIp["inet6_range"]!);
        var overlaps = SingBoxConfigBuilder.PrivateNetworkRanges
            .Where(cidr => Overlaps(inet6, IPNetwork.Parse(cidr)))
            .ToArray();
        Assert.Empty(overlaps);
        // Documented IPv6 coverage of sing-box ip_is_private (loopback, link-local, ULA); the built-in list is not in this repo.
        var ipIsPrivateV6 = new[] { "::1/128", "fe80::/10", "fc00::/7" };
        Assert.DoesNotContain(ipIsPrivateV6, cidr => Overlaps(inet6, IPNetwork.Parse(cidr)));
    }

    [Fact]
    public void Xray_default_dns_is_explicit_and_tunneled_even_with_direct_catch_all()
    {
        var settings = new AppSettings { Routing = { ProxyOnlyListed = true, ProxyDomains = ["proxy.invalid"] } };
        var json = Xray(settings);
        Assert.Equal("https://1.1.1.1/dns-query", (string?)json["dns"]!["servers"]![0]!["address"]);
        var rules = json["routing"]!["rules"]!;
        Assert.Equal("dns-remote", (string?)rules[0]!["inboundTag"]![0]);
        Assert.Equal("proxy", (string?)rules[0]!["outboundTag"]);
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

    [Theory]
    [InlineData(DnsTransport.System, "localhost")]
    [InlineData(DnsTransport.Udp, "8.8.8.8")]
    [InlineData(DnsTransport.Tcp, "tcp://8.8.8.8")]
    [InlineData(DnsTransport.Https, "https://8.8.8.8/dns-query")]
    [InlineData(DnsTransport.Quic, "quic+local://8.8.8.8")]
    public void Xray_selects_direct_dns_by_domain_and_routes_its_queries_directly(DnsTransport transport, string target)
    {
        var settings = new AppSettings { Routing =
        {
            DirectDns = "8.8.8.8", DirectDnsTransport = transport, DirectDnsDomains = ["direct.invalid"],
            RemoteDnsDomains = ["remote.invalid"],
        } };
        var json = Xray(settings);
        var servers = json["dns"]!["servers"]!;
        Assert.Equal(target, (string?)servers[0]!["address"]);
        Assert.Equal("domain:direct.invalid", (string?)servers[0]!["domains"]![0]);
        Assert.Equal("domain:remote.invalid", (string?)servers[1]!["domains"]![0]);
        Assert.True((bool)servers[0]!["skipFallback"]!);
        if (transport != DnsTransport.System)
        {
            Assert.Contains(json["routing"]!["rules"]!.AsArray(), rule =>
                (string?)rule?["inboundTag"]?[0] == "dns-local" && (string?)rule?["outboundTag"] == "direct");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Xray_reports_unsupported_dns_before_starting_a_process(bool direct, bool ruleSet)
    {
        var settings = new AppSettings();
        if (ruleSet)
        {
            if (direct) { settings.Routing.DirectDnsRuleSets = ["custom"]; }
            else { settings.Routing.RemoteDnsRuleSets = ["custom"]; }
        }
        else if (direct)
        {
            settings.Routing.DirectDns = "9.9.9.9";
            settings.Routing.DirectDnsTransport = DnsTransport.Tls;
            settings.Routing.DirectDnsDomains = ["direct.invalid"];
        }
        else { settings.Routing.RemoteDnsTransport = DnsTransport.Tls; }
        var runner = new FakeProcessRunner();
        await using var engine = new XrayEngine(runner, new LogRedactor(),
            new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        var availability = await engine.CheckAvailabilityAsync(settings);
        Assert.Equal(AvailabilityStatus.NotImplemented, availability.Status);
        Assert.Equal("routing.dns.xray", availability.LocalizationKey);
        Assert.Equal("routing.dns.xray", Assert.Throws<EngineException>(() => Xray(settings)).LocalizationKey);
        Assert.Empty(runner.Started);
    }

    // CIDR blocks are either nested or disjoint, so two networks overlap iff one contains the other's base address.
    private static bool Overlaps(IPNetwork left, IPNetwork right) =>
        left.Contains(right.BaseAddress) || right.Contains(left.BaseAddress);

    private static JsonObject Xray(AppSettings settings) =>
        new XrayConfigBuilder().Build(Server, ProfileSecrets.Empty, new BuildContext(settings));

    private static JsonNode SingBox(AppSettings settings) =>
        new SingBoxConfigBuilder().Build(Server, new ProfileSecrets { Values = { [Keys.Password] = "fake-password" } }, new BuildContext(settings))["dns"]!;
}
