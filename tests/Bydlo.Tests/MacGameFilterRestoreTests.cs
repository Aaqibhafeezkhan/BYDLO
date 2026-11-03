using Bydlo.Core.Backup;
using Bydlo.Core.Models;
using Bydlo.Engines.Zapret;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class MacGameFilterRestoreTests
{
    [Theory]
    [InlineData("disabled", BackupImportMode.Merge)]
    [InlineData("all", BackupImportMode.Merge)]
    [InlineData("tcp", BackupImportMode.Merge)]
    [InlineData("udp", BackupImportMode.Merge)]
    [InlineData("disabled", BackupImportMode.Replace)]
    [InlineData("all", BackupImportMode.Replace)]
    [InlineData("tcp", BackupImportMode.Replace)]
    [InlineData("udp", BackupImportMode.Replace)]
    public async Task Restored_filter_is_ignored_by_mac_preparation_and_preserved_for_other_platforms(string gameFilter, BackupImportMode mode)
    {
        var backup = new BackupData { Settings = new() { Zapret = new() { GameFilter = gameFilter } } };
        var restored = BackupArchive.ImportSettings(backup, new AppSettings(), mode);
        var root = TestDirs.New("mac-game-filter-restore");
        var install = new ZapretInstallation(root, Path.Combine(root, "lists"), Path.Combine(root, "tpws"), OsFamily.MacOS);
        install.EnsureUserLists();
        var backend = new PfTpwsBackend();

        foreach (var strategy in NativeZapretStrategy.Strategies)
        {
            // Preparation only: no engine, process runner, or firewall service is started.
            var baseline = await backend.PrepareAsync(install, strategy.FileName, new ZapretSettings(), CancellationToken.None);
            var parsed = await backend.PrepareAsync(install, strategy.FileName, restored.Zapret, CancellationToken.None);
            Assert.Equal(baseline.Arguments, parsed.Arguments);
            Assert.Contains("--bind-addr=127.0.0.1", parsed.Arguments);
            Assert.DoesNotContain(parsed.Arguments, a => a.Contains("1024-65535", StringComparison.Ordinal));
        }

        var lease = new ZapretFirewallLease(Guid.NewGuid().ToString("N"), OsFamily.MacOS);
        var rules = ZapretFirewallRules.Build(lease, NativeZapretStrategy.TcpPorts(restored.Zapret.GameFilter),
            NativeZapretStrategy.UdpPorts(restored.Zapret.GameFilter), []);
        Assert.Equal(ZapretFirewallRules.Build(lease, "80,443", "443", []), rules);
        Assert.Contains("port { 80,443 }", rules, StringComparison.Ordinal);

        Assert.Equal(gameFilter, restored.Zapret.GameFilter);
        Assert.Equal(gameFilter, backup.Settings.Zapret.GameFilter);
        var linux = await new NfqueueBackend().PrepareAsync(install, "native-split", restored.Zapret, CancellationToken.None);
        Assert.Contains($"--filter-tcp={NativeZapretStrategy.TcpPorts(gameFilter)}", linux.Arguments);
        Assert.Contains($"--filter-udp={NativeZapretStrategy.UdpPorts(gameFilter)}", linux.Arguments);
    }
}
