using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class MacAmneziaNetworkTests
{
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
