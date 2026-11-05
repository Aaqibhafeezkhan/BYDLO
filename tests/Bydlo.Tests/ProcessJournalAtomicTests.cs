using Bydlo.Platform.Processes;

namespace Bydlo.Tests;

/// <summary>Covers issue #343: the process journal must survive a crash mid-write,
/// and a corrupted journal must be preserved for diagnosis instead of silently dropped.</summary>
public sealed class ProcessJournalAtomicTests
{
    private static string JournalPath(string dir) => Path.Combine(dir, "journal.json");

    private static JournalEntry FakeEntry(int pid) => new(
        "retained-tun-guard", pid, "fake-engine", DateTimeOffset.UtcNow, GracefulTermination: true);

    [Fact]
    public void Round_trip_preserves_entries_and_leaves_no_temp_files()
    {
        var dir = TestDirs.New("journal-atomic-roundtrip");
        var path = JournalPath(dir);
        var journal = new ProcessJournal(path);

        journal.Record(FakeEntry(424201));
        var entries = new ProcessJournal(path).Entries();

        var single = Assert.Single(entries);
        Assert.Equal(424201, single.Pid);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp.*"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Failed_write_preserves_previous_snapshot()
    {
        var dir = TestDirs.New("journal-atomic-failure");
        var path = JournalPath(dir);
        var journal = new ProcessJournal(path);
        journal.Record(FakeEntry(424202));
        var before = File.ReadAllBytes(path);

        // Block every write path on all platforms: a read-only directory rejects
        // temp-file creation on Unix, a read-only file rejects Move on Windows.
        File.SetAttributes(path, FileAttributes.ReadOnly);
        new DirectoryInfo(dir).Attributes |= FileAttributes.ReadOnly;
        try
        {
            journal.Record(FakeEntry(424203));
        }
        finally
        {
            new DirectoryInfo(dir).Attributes &= ~FileAttributes.ReadOnly;
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        var single = Assert.Single(new ProcessJournal(path).Entries());
        Assert.Equal(424202, single.Pid);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp.*"));
    }

    [Fact]
    public void Truncated_journal_is_backed_up_for_diagnosis()
    {
        var dir = TestDirs.New("journal-atomic-corrupt");
        var path = JournalPath(dir);
        new ProcessJournal(path).Record(FakeEntry(424204));

        const string truncated = "[{\"DisplayName\":\"retained-tun-guard\",\"Pid\":424204,";
        File.WriteAllText(path, truncated);

        var entries = new ProcessJournal(path).Entries();

        Assert.Empty(entries);
        var corrupt = Directory.GetFiles(dir, "journal.json.corrupt-*.json");
        var backup = Assert.Single(corrupt);
        Assert.Equal(truncated, File.ReadAllText(backup));
    }

    [Fact]
    public void Journal_recovers_after_corruption_while_evidence_is_kept()
    {
        var dir = TestDirs.New("journal-atomic-recover");
        var path = JournalPath(dir);
        var journal = new ProcessJournal(path);
        journal.Record(FakeEntry(424205));

        File.WriteAllText(path, "{\"truncated\":");
        Assert.Empty(new ProcessJournal(path).Entries());

        journal.Record(FakeEntry(424206));

        var single = Assert.Single(new ProcessJournal(path).Entries());
        Assert.Equal(424206, single.Pid);
        // Entries() above already saved one corrupt copy; Record() re-reads before
        // writing and saves another, so accept >= 1 (evidence is kept either way).
        Assert.True(Directory.GetFiles(dir, "journal.json.corrupt-*.json").Length >= 1);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp.*"));
    }
}
