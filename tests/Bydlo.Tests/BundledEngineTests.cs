using System.Net;
using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines;
using Bydlo.Engines.Amnezia;
using Bydlo.Engines.Installation;
using Bydlo.Engines.OpenVpn;
using Bydlo.Platform;

namespace Bydlo.Tests;

public class BundledEngineTests
{
    private static string Config => $"""
        [Interface]
        PrivateKey = {Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())}
        Address = 10.5.0.2/24, fd00::2/64
        DNS = 1.1.1.1
        Jc = 4
        H1 = 1234
        [Peer]
        PublicKey = {Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray())}
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = vpn.example:51820
        PersistentKeepalive = 25
        """;

    [Theory]
    [InlineData(OsFamily.Windows, CpuArch.X64)]
    [InlineData(OsFamily.Windows, CpuArch.Arm64)]
    [InlineData(OsFamily.Linux, CpuArch.X64)]
    [InlineData(OsFamily.Linux, CpuArch.Arm64)]
    [InlineData(OsFamily.MacOS, CpuArch.X64)]
    [InlineData(OsFamily.MacOS, CpuArch.Arm64)]
    public void Bundled_copy_wins_over_managed_install_but_explicit_override_wins(OsFamily os, CpuArch arch)
    {
        var root = TestDirs.New("bundle-locator");
        var engineLock = EngineLock.LoadEmbedded();
        var platform = new PlatformInfo(os, arch);
        var managedRoot = Path.Combine(root, "managed");
        var locator = new EngineLocator(new EngineInstaller(new HttpClient(), engineLock, managedRoot), platform, root);
        foreach (var (kind, id) in new[] { (EngineKind.SingBox, "sing-box"), (EngineKind.Xray, "xray"), (EngineKind.OpenVpn, "openvpn"), (EngineKind.AmneziaWG, "amneziawg") })
        {
            var entry = engineLock.Find(id)!;
            var relative = entry.Assets.TryGetValue(platform.Rid, out var asset) ? asset.Executable : entry.BundledExecutables.GetValueOrDefault(platform.Rid);
            if (relative is null) { Assert.Equal(OsFamily.MacOS, os); continue; }
            relative = relative.Replace('/', Path.DirectorySeparatorChar);
            var bundled = Path.Combine(root, "engines", id, entry.Version, relative);
            var managed = Path.Combine(managedRoot, id, entry.Version, relative);
            foreach (var path in new[] { bundled, managed }) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture, never execute"); }
            Assert.Equal(bundled, locator.Locate(kind, new()));
            var settings = new AppSettings();
            settings.EnginePaths[kind.ToString()] = managed;
            Assert.Equal(managed, locator.Locate(kind, settings));
            File.Delete(bundled);
            Assert.Equal(managed, locator.Locate(kind, new()));
        }
    }

    [Fact]
    public async Task Amnezia_converts_secrets_and_resolves_endpoint_before_routes_are_installed()
    {
        var config = await AmneziaConfig.ParseAsync(Config, (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.20") }), default);
        Assert.Contains("private_key=" + string.Concat(Enumerable.Repeat("01", 32)), config.Uapi, StringComparison.Ordinal);
        Assert.Contains("endpoint=203.0.113.20:51820", config.Uapi, StringComparison.Ordinal);
        Assert.Contains("jc=4", config.Uapi, StringComparison.Ordinal);
        Assert.Contains("persistent_keepalive_interval=25", config.Uapi, StringComparison.Ordinal);
        Assert.Equal(["10.5.0.2/24", "fd00::2/64"], config.Addresses);
        Assert.Equal(["0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1"], config.Routes);
        Assert.DoesNotContain("vpn.example", config.Uapi, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PostUp = evil-command")]
    [InlineData("Table = off")]
    [InlineData("SaveConfig = true")]
    public async Task Amnezia_rejects_unimplemented_hooks_and_routing_options(string option)
    {
        await Assert.ThrowsAsync<FormatException>(() => AmneziaConfig.ParseAsync(Config.Replace("Jc = 4", option, StringComparison.Ordinal),
            (_, _) => throw new InvalidOperationException("Must fail before DNS"), default));
    }

    [Fact]
    public async Task Amnezia_masks_allowed_networks_without_masking_the_interface_address()
    {
        var config = await AmneziaConfig.ParseAsync(Config.Replace("0.0.0.0/0, ::/0", "10.20.30.99/24", StringComparison.Ordinal),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.20") }), default);
        Assert.Equal(["10.20.30.0/24"], config.Routes);
        Assert.Contains("allowed_ip=10.20.30.0/24", config.Uapi, StringComparison.Ordinal);
        Assert.Equal("10.5.0.2/24", config.Addresses[0]);
        await Assert.ThrowsAsync<FormatException>(() => AmneziaConfig.ParseAsync(Config.Replace("AllowedIPs = 0.0.0.0/0, ::/0", "", StringComparison.Ordinal),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.20") }), default));
    }

    [Theory]
    [InlineData("amneziawg.exe")]
    [InlineData("awg-quick")]
    public async Task Amnezia_rejects_legacy_custom_client_before_starting_it(string path)
    {
        var runner = new FakeProcessRunner();
        var engine = new AmneziaEngine(runner, new LogRedactor(), new FakeLocator(new(OsFamily.Windows, CpuArch.X64), path), new FakePrivileges(true), new FakeAmneziaNetwork());
        Assert.Equal("error.engine.amnezia.legacy", (await engine.CheckAvailabilityAsync(new())).LocalizationKey);
        Assert.Empty(runner.Started);
    }

    [Fact]
    public async Task Amnezia_supervises_headless_process_and_retains_cleanup_for_retry()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "BYDLO_AMNEZIA_READY" } };
        var network = new FakeAmneziaNetwork();
        var engine = new AmneziaEngine(runner, new LogRedactor(), new FakeLocator(new(OsFamily.Windows, CpuArch.X64)),
            new FakePrivileges(true), network, (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.20") }));
        var work = TestDirs.New("amnezia-engine");
        await engine.StartAsync(new() { Settings = new(), WorkDirectory = work, Secrets = new() { Values = { [Keys.RawConfig] = Config } } });
        Assert.True(network.Prepared && network.Applied);
        Assert.Equal(EngineState.Running, engine.State);
        var spec = Assert.Single(runner.Started);
        Assert.DoesNotContain("installtunnelservice", string.Join(' ', spec.Arguments), StringComparison.Ordinal);
        Assert.Empty(runner.Completed);
        Assert.Contains(Environment.ProcessId.ToString(), spec.Arguments);
        network.FailCleanup = true;
        await Assert.ThrowsAsync<AggregateException>(() => engine.StopAsync());
        Assert.True(File.Exists(Path.Combine(work, "amnezia.json")));
        network.FailCleanup = false;
        await engine.StopAsync();
        Assert.False(File.Exists(Path.Combine(work, "amnezia.json")));
        Assert.True(runner.Last!.Stopped);
    }

    [Fact]
    public async Task Network_cleanup_retains_journal_on_failure_and_recovers_from_new_instance()
    {
        var root = TestDirs.New("amnezia-recovery");
        var journal = Path.Combine(root, "amnezia-network.json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new AmneziaNetworkLease(OsFamily.Windows,
            [new("203.0.113.20", "192.0.2.1", "7")], Guid.NewGuid().ToString())));
        var runner = new FakeProcessRunner { CompletionExitCode = 1 };
        var network = new AmneziaNetwork(runner, OsFamily.Windows, root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => network.RemoveAsync());
        Assert.True(File.Exists(journal));
        runner.CompletionExitCode = 0;
        await network.DisposeAsync();
        Assert.False(File.Exists(journal));
        Assert.All(runner.Completed, p => Assert.True(p.AffectsNetwork));
        Assert.All(runner.Completed, p => Assert.DoesNotContain("Remove-NetIPAddress", string.Join(' ', p.Arguments), StringComparison.Ordinal));
        // A new application instance can replay a durable, interrupted cleanup.
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new AmneziaNetworkLease(OsFamily.Windows,
            [new("203.0.113.20", "192.0.2.1", "7")])));
        await using var recovered = new AmneziaNetwork(runner, OsFamily.Windows, root);
        await recovered.RemoveAsync();
        Assert.False(File.Exists(journal));
    }

    [Theory]
    [InlineData(OsFamily.Windows)]
    [InlineData(OsFamily.Linux)]
    public async Task Network_lease_preserves_host_addresses_and_scopes_all_dns_and_route_changes(OsFamily os)
    {
        var guid = Guid.NewGuid().ToString();
        var runner = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var command = string.Join(' ', spec.Arguments);
                if (command.Contains("Find-NetRoute", StringComparison.Ordinal)) { return (0, "{\"gateway\":\"192.0.2.1\",\"dev\":\"7\"}"); }
                if (command.Contains("InterfaceGuid.ToString", StringComparison.Ordinal)) { return (0, guid); }
                if (command.Contains(").Count", StringComparison.Ordinal)) { return (0, "0"); }
                if (command.Contains("route get", StringComparison.Ordinal)) { return (0, "[{\"gateway\":\"192.0.2.1\",\"dev\":\"eth0\"}]"); }
                if (command == "-j link show dev bydlo-awg") { return (0, "[{\"ifindex\":42}]"); }
                if (command == "-j link show") { return (0, "[]"); }
                return (0, "");
            },
        };
        await using var network = new AmneziaNetwork(runner, os, TestDirs.New("amnezia-plan"));
        await network.PrepareAsync(["203.0.113.20"], default);
        Assert.True(network.HasPendingRecovery);
        await network.ApplyAsync(["10.5.0.2/24"], ["1.1.1.1"], ["0.0.0.0/1", "128.0.0.0/1"], 1420, default);
        var commands = runner.Completed.Select(p => string.Join(' ', p.Arguments)).ToArray();
        Assert.Contains(commands, c => c.Contains("10.5.0.2", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("10.5.0.0", StringComparison.Ordinal));
        Assert.Contains(commands, c => c.Contains("203.0.113.20/32", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("route replace", StringComparison.Ordinal) || c.Contains("route flush", StringComparison.Ordinal));
        Assert.All(runner.Completed, p => Assert.True(p.AffectsNetwork));
        await network.RemoveAsync();
        Assert.False(network.HasPendingRecovery);
    }

    [Fact]
    public async Task Driver_setup_requires_consent_and_cannot_bypass_development_safety_lock()
    {
        var runner = new FakeProcessRunner();
        var driver = new OpenVpnDriver(runner, new FakePrivileges(true), () => throw new InvalidOperationException("Must not inspect bundle"), () => false);
        await Assert.ThrowsAsync<EngineException>(() => driver.InstallAsync(false));
        Assert.True(NetworkSafetyGuard.IsLocked); // scripts/env.* is mandatory for these tests.
        await Assert.ThrowsAsync<NetworkSafetyException>(() => driver.InstallAsync(true));
        Assert.Empty(runner.Completed);
    }

    [Fact]
    public async Task Openvpn_availability_does_not_install_driver_and_windows_launch_uses_bundled_dco_adapter()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Initialization Sequence Completed" } };
        var driver = new FakeDriver();
        var engine = new OpenVpnEngine(runner, new LogRedactor(), new FakeLocator(new(OsFamily.Windows, CpuArch.X64)), new FakePrivileges(true), driver);
        Assert.Equal("error.engine.openvpn.driver", (await engine.CheckAvailabilityAsync(new())).LocalizationKey);
        Assert.Empty(runner.Completed);
        driver.IsInstalled = true;
        await engine.StartAsync(new() { Settings = new(), WorkDirectory = TestDirs.New("openvpn-bundle"), Secrets = new() { Values = { [Keys.RawConfig] = "client\ndev tun\nremote 203.0.113.20 1194" } } });
        var spec = Assert.Single(runner.Started);
        Assert.Contains("ovpn-dco", spec.Arguments);
        Assert.Contains(OpenVpnDriver.AdapterName, spec.Arguments);
        Assert.True(spec.GracefulTermination);
        await engine.DisposeAsync();
    }

    private sealed class FakeDriver : IOpenVpnDriver
    {
        public bool IsInstalled { get; set; }
        public Task InstallAsync(bool userConsent, CancellationToken ct = default) => throw new InvalidOperationException("Never install from an engine");
    }

    private sealed class FakeAmneziaNetwork : IAmneziaNetwork
    {
        public bool Prepared { get; private set; }
        public bool Applied { get; private set; }
        public bool FailCleanup { get; set; }
        public bool HasPendingRecovery => Prepared;
        public Task PrepareAsync(IReadOnlyList<string> endpoints, CancellationToken ct) { Prepared = true; return Task.CompletedTask; }
        public Task ApplyAsync(IReadOnlyList<string> addresses, IReadOnlyList<string> dns, IReadOnlyList<string> routes, int mtu, CancellationToken ct) { Applied = true; return Task.CompletedTask; }
        public Task RemoveAsync(CancellationToken ct = default) { if (FailCleanup) { throw new IOException("fixture cleanup failure"); } Prepared = false; return Task.CompletedTask; }
    }
}
