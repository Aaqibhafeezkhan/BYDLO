using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Isolated tests for issue #10 (per-server latency, best server). No sockets are opened.</summary>
public sealed class ServerLatencyTests
{
    private static Profile Server(string name, int port = 443, ProtocolKind protocol = ProtocolKind.Vless) =>
        new() { Name = name, Protocol = protocol, Host = "example.com", Port = port };

    private static ServerLatencyProbe Fake(Dictionary<string, TimeSpan?> byHost, Action? onCall = null) =>
        new((host, port, timeout, ct) =>
        {
            onCall?.Invoke();
            return Task.FromResult(byHost.TryGetValue(host, out var latency) ? latency : null);
        });

    [Fact]
    public async Task Probe_maps_reachable_and_unreachable_servers()
    {
        var probe = Fake(new Dictionary<string, TimeSpan?> { ["a.example"] = TimeSpan.FromMilliseconds(120) });
        var a = new Profile { Name = "a", Protocol = ProtocolKind.Vless, Host = "a.example", Port = 443 };
        var b = new Profile { Name = "b", Protocol = ProtocolKind.Vless, Host = "b.example", Port = 443 };
        var results = await probe.ProbeAsync([a, b]);
        Assert.Equal(2, results.Count);
        Assert.True(results[a.Id].Reachable);
        Assert.Equal(TimeSpan.FromMilliseconds(120), results[a.Id].Latency);
        Assert.False(results[b.Id].Reachable);
    }

    [Fact]
    public async Task Probe_skips_invalid_endpoints_without_connecting()
    {
        var calls = 0;
        var probe = Fake(new Dictionary<string, TimeSpan?>(), () => calls++);
        var results = await probe.ProbeAsync(
        [
            new Profile { Name = "empty", Protocol = ProtocolKind.Vless, Host = "", Port = 443 },
            new Profile { Name = "bad-port", Protocol = ProtocolKind.Vless, Host = "example.com", Port = 0 },
        ]);
        Assert.Equal(0, calls);
        Assert.All(results.Values, r => Assert.False(r.Reachable));
    }

    [Fact]
    public async Task Probe_treats_failures_as_unreachable()
    {
        var probe = new ServerLatencyProbe((host, port, timeout, ct) => Task.FromResult<TimeSpan?>(null));
        var results = await probe.ProbeAsync([Server("x")]);
        Assert.False(Assert.Single(results.Values).Reachable);
    }

    [Fact]
    public async Task Probe_treats_timeout_exceptions_as_unreachable()
    {
        var probe = new ServerLatencyProbe((host, port, timeout, ct) => throw new TimeoutException("slow"));
        var results = await probe.ProbeAsync([Server("x")]);
        Assert.False(Assert.Single(results.Values).Reachable);
    }

    [Fact]
    public async Task Real_probe_refuses_to_run_under_the_safety_lock()
    {
        Assert.True(NetworkSafetyGuard.IsLocked, "Tests must run with BYDLO_SAFE_DEV=1 (scripts/env).");
        var probe = new ServerLatencyProbe();
        await Assert.ThrowsAsync<NetworkSafetyException>(() => probe.ProbeAsync([Server("x")]));
    }

    [Fact]
    public void Udp_protocols_are_marked_approximate()
    {
        Assert.False(ServerLatencyProbe.IsApproximate(ProtocolKind.Vless));
        Assert.False(ServerLatencyProbe.IsApproximate(ProtocolKind.Trojan));
        Assert.True(ServerLatencyProbe.IsApproximate(ProtocolKind.Hysteria2));
        Assert.True(ServerLatencyProbe.IsApproximate(ProtocolKind.Tuic));
        Assert.True(ServerLatencyProbe.IsApproximate(ProtocolKind.WireGuard));
        Assert.True(ServerLatencyProbe.IsApproximate(ProtocolKind.AmneziaWG));
    }

    [Fact]
    public void PickBest_prefers_minimum_latency_and_ignores_unreachable()
    {
        var fast = Server("fast");
        var slow = Server("slow");
        var down = Server("down");
        var latencies = new Dictionary<Guid, ServerLatencyResult>
        {
            [slow.Id] = new(true, TimeSpan.FromMilliseconds(900), false),
            [down.Id] = new(false, TimeSpan.Zero, false),
            [fast.Id] = new(true, TimeSpan.FromMilliseconds(50), false),
        };
        Assert.Same(fast, BestServerPicker.PickBest([slow, down, fast], latencies));
    }

    [Fact]
    public void PickBest_returns_null_without_reachable_servers()
    {
        var lonely = Server("lonely");
        Assert.Null(BestServerPicker.PickBest([lonely], new Dictionary<Guid, ServerLatencyResult>()));
        Assert.Null(BestServerPicker.PickBest([lonely],
            new Dictionary<Guid, ServerLatencyResult> { [lonely.Id] = new(false, TimeSpan.Zero, false) }));
    }

    [Fact]
    public void Ordering_defaults_to_favorites_then_name()
    {
        var b = Server("bravo");
        var a = Server("alpha");
        var fav = Server("zulu");
        fav.IsFavorite = true;
        var ordered = ServerListOrdering.Order([b, a, fav], new Dictionary<Guid, ServerLatencyResult>(), sortByLatency: false);
        Assert.Equal(["zulu", "alpha", "bravo"], ordered.Select(p => p.Name));
    }

    [Fact]
    public void Ordering_by_latency_puts_unreachable_last()
    {
        var slow = Server("slow");
        var fast = Server("fast");
        var down = Server("down");
        var latencies = new Dictionary<Guid, ServerLatencyResult>
        {
            [slow.Id] = new(true, TimeSpan.FromMilliseconds(900), false),
            [fast.Id] = new(true, TimeSpan.FromMilliseconds(50), false),
            [down.Id] = new(false, TimeSpan.Zero, false),
        };
        var ordered = ServerListOrdering.Order([slow, down, fast], latencies, sortByLatency: true);
        Assert.Equal(["fast", "slow", "down"], ordered.Select(p => p.Name));
    }
}
