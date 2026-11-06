using Bydlo.App.Services;
using Bydlo.Core.Security;
using Bydlo.Platform.Processes;

namespace Bydlo.Tests;

/// <summary>Regression tests for issue #349: a failed orphan stop must not hide the banner.</summary>
public sealed class OrphansRescanTests
{
    [Fact]
    public async Task Cleanup_clears_dead_entries_and_returns_zero()
    {
        var dir = TestDirs.New("orphans-rescan-dead");
        var previousDataDir = Environment.GetEnvironmentVariable("BYDLO_DATA_DIR");
        Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", dir);
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var services = await AppServices.CreateAsync([], new FakePrivileges(false));
            try
            {
                // Keep the real safety lock active; only simulate the termination step.
                services.ReplaceJournalForTests(new DeadEntryJournal(Path.Combine(dir, "dead-journal.json")));
                // Dead PID: no such process, so FindOrphans must drop it.
                services.Journal.Record(new JournalEntry("dead-engine", int.MaxValue, "dead-image", DateTimeOffset.Now));
                Assert.Single(services.Journal.Entries());

                var killed = services.CleanupOrphans();

                Assert.Equal(0, killed);
                Assert.Empty(services.Orphans);
                Assert.Empty(services.Journal.Entries());
            }
            finally
            {
                await services.ShutdownAsync();
            }
        }
        finally
        {
            NetworkSafetyGuard.OverrideForTests(null);
            Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", previousDataDir);
        }
    }

    [Fact]
    public async Task Cleanup_keeps_remaining_and_throws_stop_error()
    {
        var dir = TestDirs.New("orphans-rescan-stuck");
        var previousDataDir = Environment.GetEnvironmentVariable("BYDLO_DATA_DIR");
        Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", dir);
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var services = await AppServices.CreateAsync([], new FakePrivileges(false));
            try
            {
                var remaining = new JournalEntry("stuck-engine", 12345, "stuck-image", DateTimeOffset.Now);
                services.ReplaceJournalForTests(new StuckJournal(
                    Path.Combine(dir, "stub-journal.json"), killed: 1, remaining: [remaining]));

                var ex = Assert.Throws<InvalidOperationException>(() => services.CleanupOrphans());
                Assert.Equal(Loc.Instance["error.orphans.stop"], ex.Message);
                Assert.Single(services.Orphans);
                Assert.Equal("stuck-engine", services.Orphans[0].DisplayName);
            }
            finally
            {
                await services.ShutdownAsync();
            }
        }
        finally
        {
            NetworkSafetyGuard.OverrideForTests(null);
            Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", previousDataDir);
        }
    }

    [Fact]
    public async Task Cleanup_success_with_stub_returns_killed_and_clears()
    {
        var dir = TestDirs.New("orphans-rescan-ok");
        var previousDataDir = Environment.GetEnvironmentVariable("BYDLO_DATA_DIR");
        Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", dir);
        NetworkSafetyGuard.OverrideForTests(true);
        try
        {
            var services = await AppServices.CreateAsync([], new FakePrivileges(false));
            try
            {
                services.ReplaceJournalForTests(new StuckJournal(
                    Path.Combine(dir, "stub-journal.json"), killed: 2, remaining: []));

                var killed = services.CleanupOrphans();

                Assert.Equal(2, killed);
                Assert.Empty(services.Orphans);
            }
            finally
            {
                await services.ShutdownAsync();
            }
        }
        finally
        {
            NetworkSafetyGuard.OverrideForTests(null);
            Environment.SetEnvironmentVariable("BYDLO_DATA_DIR", previousDataDir);
        }
    }

    private sealed class DeadEntryJournal(string filePath) : ProcessJournal(filePath)
    {
        public override int KillOrphans(string? displayName = null)
        {
            Assert.True(NetworkSafetyGuard.IsLocked);
            // CleanupOrphans performs the real read-only rescan after this fake termination step.
            return 0;
        }
    }

    private sealed class StuckJournal(string filePath, int killed, IReadOnlyList<JournalEntry> remaining)
        : ProcessJournal(filePath)
    {
        public override int KillOrphans(string? displayName = null) => killed;

        public override IReadOnlyList<JournalEntry> FindOrphans() => remaining;
    }
}
