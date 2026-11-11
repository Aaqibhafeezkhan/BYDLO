using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Amnezia;
using Bydlo.Helper;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class AmneziaHelperDnsTests
{
    private static string Config => $"""
        [Interface]
        PrivateKey = {Convert.ToBase64String(new byte[32])}
        Address = 10.5.0.2/24
        DNS = 1.1.1.1, 2606:4700:4700::1111
        Jc = 4
        [Peer]
        PublicKey = {Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray())}
        AllowedIPs = 0.0.0.0/0
        Endpoint = 203.0.113.20:51820
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("dns")]
    [InlineData("domain")]
    public async Task Linux_dns_passes_helper_policy_and_failed_setup_rolls_back(string? failedCommand)
    {
        var linkExists = false;
        var commands = new FakeProcessRunner
        {
            OnCompletion = spec =>
            {
                var command = string.Join(' ', spec.Arguments);
                if (Path.GetFileName(spec.FileName) == "resolvectl")
                {
                    Assert.Equal("amnezia-network", spec.DisplayName);
                    Assert.Equal(Path.Combine("/usr/bin", "resolvectl"), spec.FileName);
                    return (spec.Arguments[0] == failedCommand ? 1 : 0, "");
                }
                if (command.Contains("route get", StringComparison.Ordinal)) { return (0, "[{\"gateway\":\"192.0.2.1\",\"dev\":\"eth0\"}]"); }
                if (command == "-j link show dev bydlo-awg") { return (0, "[{\"ifindex\":42}]"); }
                if (command == "-j link show") { return (0, linkExists ? "[{\"ifname\":\"bydlo-awg\",\"ifindex\":42}]" : "[]"); }
                if (command.Contains("route show", StringComparison.Ordinal)) { return (0, linkExists ? "203.0.113.20 via 192.0.2.1 dev eth0" : ""); }
                if (command == "link delete dev bydlo-awg") { linkExists = false; }
                return (0, "");
            },
        };
        // The production resolution/allowlist is used, but only a fake can execute these specs.
        await using var network = new AmneziaNetwork(new PolicyRunner(commands), OsFamily.Linux, TestDirs.New("helper-dns"));
        var process = new FakeProcessRunner
        {
            StartupOutput = { "BYDLO_AMNEZIA_READY" },
            OnStart = _ => linkExists = true,
        };
        await using var engine = new AmneziaEngine(process, new LogRedactor(), new FakeLocator(new(OsFamily.Linux, CpuArch.X64)),
            new FakePrivileges(true), network);
        var work = TestDirs.New("helper-dns-engine");
        var request = new EngineStartRequest { Settings = new(), WorkDirectory = work, Secrets = new() { Values = { [Keys.RawConfig] = Config } } };
        if (failedCommand is null)
        {
            await engine.StartAsync(request);
            Assert.Equal(EngineState.Running, engine.State);
            var dns = commands.Completed.Where(s => Path.GetFileName(s.FileName) == "resolvectl").ToArray();
            Assert.Equal(2, dns.Length);
            Assert.Equal(["dns", "bydlo-awg", "1.1.1.1", "2606:4700:4700::1111"], dns[0].Arguments);
            Assert.Equal(["domain", "bydlo-awg", "~."], dns[1].Arguments);
            await engine.StopAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request));
            Assert.Equal("error.engine.amnezia.network", error.LocalizationKey);
            Assert.Equal(EngineState.Failed, engine.State);
        }
        Assert.Contains(commands.Completed, s => string.Join(' ', s.Arguments) == "link delete dev bydlo-awg");
        Assert.False(linkExists);
        Assert.False(network.HasPendingRecovery);
        Assert.True(process.Last!.Stopped);
        Assert.False(File.Exists(Path.Combine(work, "amnezia.json")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("untrusted")]
    public void Untagged_dns_is_not_a_helper_system_operation(string displayName)
    {
        Assert.Null(HelperServer.ResolveUnixSystemTool(new() { FileName = "resolvectl", DisplayName = displayName },
            _ => throw new InvalidOperationException("Must not search for untagged commands")));
    }

    [Fact]
    public void Helper_policy_still_rejects_unapproved_and_missing_tools()
    {
        Assert.Throws<UnauthorizedAccessException>(() => HelperServer.ResolveUnixSystemTool(
            new() { FileName = "sh", DisplayName = "amnezia-network" }, _ => true));
        Assert.Throws<FileNotFoundException>(() => HelperServer.ResolveUnixSystemTool(
            new() { FileName = "resolvectl", DisplayName = "amnezia-network" }, _ => false));
    }

    [Fact]
    public async Task Production_helper_runner_remains_safety_locked_with_a_fake_executor()
    {
        Assert.True(NetworkSafetyGuard.IsLocked);
        var fake = new FakeProcessRunner();
        var runner = new HelperServer.VerifiedHelperRunner(fake);
        await Assert.ThrowsAsync<NetworkSafetyException>(() => runner.RunToCompletionAsync(
            new() { FileName = "never-execute" }, TimeSpan.FromSeconds(1)));
        Assert.Empty(fake.Completed);
    }

    private sealed class PolicyRunner(FakeProcessRunner fake) : IProcessRunner
    {
        public IManagedProcess Start(ProcessSpec spec) => throw new InvalidOperationException("Only fake completion is allowed");

        public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default)
        {
            var resolved = HelperServer.ResolveUnixSystemTool(spec, path => path == Path.Combine("/usr/bin", Path.GetFileName(spec.FileName)))
                ?? throw new UnauthorizedAccessException("Not a helper system operation");
            return fake.RunToCompletionAsync(resolved, timeout, ct);
        }
    }
}
