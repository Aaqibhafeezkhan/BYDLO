using Bydlo.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public sealed class MacAmneziaNetworkTests
{
    [Fact]
    public async Task Failed_recovery_retains_journal_and_exclusive_ownership_until_retry()
    {
        var root = TestDirs.New("mac-awg-retry");
        var journal = Path.Combine(root, "amnezia-macos-network.json");
        await File.WriteAllTextAsync(journal, """
            {"Endpoints":[],"Dns":[{"Service":"Wi-Fi","Previous":[],"Applied":["1.1.1.1"]}]}
            """);
        var fail = true;
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec => spec.Arguments[0] == "-getdnsservers" ? (0, "1.1.1.1")
                : fail ? (1, "fixture failure") : (0, ""),
        };
        var network = new MacAmneziaNetwork(runner, root, () => { });
        await Assert.ThrowsAsync<IOException>(() => network.PrepareAsync([], default));
        Assert.True(network.HasPendingRecovery);

        var otherRunner = new FakeProcessRunner();
        var other = new MacAmneziaNetwork(otherRunner, root, () => { });
        await Assert.ThrowsAsync<IOException>(() => other.PrepareAsync([], default));
        Assert.Empty(otherRunner.Completed);

        fail = false;
        await network.RemoveAsync();
        Assert.False(network.HasPendingRecovery);
        Assert.Contains(runner.Completed, spec => spec.Arguments.SequenceEqual(new[] { "-setdnsservers", "Wi-Fi", "Empty" }));
        await other.PrepareAsync([], default);
        await other.RemoveAsync();
    }

    [Fact]
    public async Task Recovery_from_new_instance_preserves_dns_and_routes_changed_by_another_owner()
    {
        var root = TestDirs.New("mac-awg-foreign");
        await File.WriteAllTextAsync(Path.Combine(root, "amnezia-macos-network.json"), """
            {"Endpoints":[{"Address":"192.0.2.2","Interface":"en0","Gateway":"192.0.2.1"}],
             "Dns":[{"Service":"Wi-Fi","Previous":["9.9.9.9"],"Applied":["1.1.1.1"]}]}
            """);
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec => spec.FileName == "networksetup" ? (0, "8.8.8.8")
                : (0, "destination: 192.0.2.2\ngateway: 192.0.2.1\ninterface: en0\nflags: <UP,HOST>"),
        };
        var network = new MacAmneziaNetwork(runner, root, () => { });
        await network.RemoveAsync();
        Assert.False(network.HasPendingRecovery);
        Assert.Equal(2, runner.Completed.Count);
        Assert.All(runner.Completed, spec => Assert.False(spec.AffectsNetwork));
        await network.RemoveAsync();
        Assert.Equal(2, runner.Completed.Count);
    }

    [Fact]
    public async Task Applies_ipv6_and_direct_peer_route_without_touching_default_route()
    {
        var runner = new FakeProcessRunner
        {
            OnCompletion = _ => (0, "destination: default\ngateway: link#4\ninterface: en0\nflags: <UP>"),
        };
        var network = new MacAmneziaNetwork(runner, TestDirs.New("mac-awg-ipv6"), () => { });
        await network.PrepareAsync(["2001:db8::2"], default);
        await network.ApplyToInterfaceAsync("utun7", ["fd00::2/64"], [], ["::/0"], 1280, default);
        Assert.Contains(runner.Completed, spec => spec.FileName == "ifconfig" &&
            spec.Arguments.SequenceEqual(new[] { "utun7", "inet6", "fd00::2", "prefixlen", "64", "alias" }));
        Assert.Contains(runner.Completed, spec => spec.FileName == "route" &&
            spec.Arguments.SequenceEqual(new[] { "-n", "add", "-inet6", "-host", "2001:db8::2", "-interface", "en0", "-proto2" }));
        foreach (var route in new[] { "::/1", "8000::/1" })
        {
            Assert.Contains(runner.Completed, spec => spec.FileName == "route" &&
                spec.Arguments.SequenceEqual(new[] { "-n", "add", "-inet6", "-net", route, "-interface", "utun7" }));
        }
        Assert.DoesNotContain(runner.Completed, spec => spec.FileName == "networksetup" || spec.Arguments.Contains("::/0"));
        await network.RemoveAsync();
        Assert.Empty(runner.Started);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("en0")]
    [InlineData("utun")]
    [InlineData("utun-1")]
    [InlineData("utun3\n")]
    public async Task Rejects_invalid_interface_before_any_command(string? name)
    {
        var runner = new FakeProcessRunner();
        var network = new MacAmneziaNetwork(runner, TestDirs.New("mac-awg-name"), () => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => network.ApplyToInterfaceAsync(name, [], [], [], 1420, default));
        Assert.Empty(runner.Completed);
    }

    [Fact]
    public async Task Safety_guard_blocks_prepare_apply_and_recovery_before_any_command()
    {
        var root = TestDirs.New("mac-awg-safe");
        await File.WriteAllTextAsync(Path.Combine(root, "amnezia-macos-network.json"), "{\"Endpoints\":[],\"Dns\":[]}");
        var runner = new FakeProcessRunner();
        var network = new MacAmneziaNetwork(runner, root);
        await Assert.ThrowsAsync<NetworkSafetyException>(() => network.PrepareAsync([], default));
        await Assert.ThrowsAsync<NetworkSafetyException>(() => network.ApplyToInterfaceAsync("utun3", [], [], [], 1420, default));
        await Assert.ThrowsAsync<NetworkSafetyException>(() => network.RemoveAsync());
        Assert.True(network.HasPendingRecovery);
        Assert.Empty(runner.Completed);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Applies_utun_routes_and_recovers_only_its_peer_route_and_dns()
    {
        var peerRouteAdded = false;
        var dns = "9.9.9.9";
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var args = spec.Arguments;
                if (spec.FileName == "route" && args[1] == "get")
                {
                    Assert.False(spec.AffectsNetwork);
                    return (0, peerRouteAdded
                        ? "destination: 192.0.2.2\ngateway: 192.0.2.1\ninterface: en0\nflags: <UP,HOST,PROTO2>"
                        : "destination: default\ngateway: 192.0.2.1\ninterface: en0\nflags: <UP,GATEWAY>");
                }
                if (spec.FileName == "route" && args.Contains("-proto2"))
                {
                    Assert.True(spec.AffectsNetwork);
                    peerRouteAdded = args[1] == "add";
                }
                if (spec.FileName == "scutil") { return (0, "Network interfaces: en0"); }
                if (spec.FileName == "networksetup" && args[0] == "-listnetworkserviceorder")
                {
                    return (0, "(1) Wi-Fi\n(Hardware Port: Wi-Fi, Device: en0)");
                }
                if (spec.FileName == "networksetup" && args[0] == "-getdnsservers") { return (0, dns); }
                if (spec.FileName == "networksetup" && args[0] == "-setdnsservers")
                {
                    Assert.True(spec.AffectsNetwork);
                    Assert.Equal("Wi-Fi", args[1]);
                    dns = args[2];
                }
                return (0, "");
            },
        };
        var network = new MacAmneziaNetwork(runner, TestDirs.New("mac-awg"), () => { });
        await network.PrepareAsync(["192.0.2.2"], default);
        await network.ApplyToInterfaceAsync("utun3", ["10.20.30.1/24"], ["1.1.1.1"], ["0.0.0.0/0"], 1420, default);

        Assert.True(peerRouteAdded);
        Assert.Equal("1.1.1.1", dns);
        Assert.Contains(runner.Completed, spec => spec.FileName == "ifconfig" && spec.Arguments.Contains("255.255.255.0"));
        Assert.Contains(runner.Completed, spec => spec.FileName == "route" &&
            spec.Arguments.Contains("0.0.0.0/1") && spec.Arguments.Contains("utun3"));
        Assert.Contains(runner.Completed, spec => spec.FileName == "route" && spec.Arguments.Contains("128.0.0.0/1"));

        await network.RemoveAsync();
        Assert.False(peerRouteAdded);
        Assert.Equal("9.9.9.9", dns);
        Assert.False(network.HasPendingRecovery);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Leaves_an_existing_peer_route_untouched()
    {
        var runner = new FakeProcessRunner
        {
            OnCompletion = _ => (0, "destination: 192.0.2.2\ngateway: 192.0.2.1\ninterface: en0\nflags: <UP,HOST>"),
        };
        var network = new MacAmneziaNetwork(runner, TestDirs.New("mac-awg"), () => { });
        await network.PrepareAsync(["192.0.2.2"], default);
        await network.RemoveAsync();
        Assert.Single(runner.Completed);
        Assert.False(runner.Completed[0].AffectsNetwork);
    }
}
