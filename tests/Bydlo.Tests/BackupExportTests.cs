using Bydlo.Core.Backup;

namespace Bydlo.Tests;

/// <summary>Issue 334: interrupted export must not destroy the previous backup.</summary>
public sealed class BackupExportTests
{
    [Fact]
    public async Task Overwrite_replaces_content_atomically_without_temp_leftovers()
    {
        var dir = TestDirs.New("backup-atomic");
        var path = Path.Combine(dir, "BYDLO.bydlobackup");
        await File.WriteAllBytesAsync(path, "old-backup"u8.ToArray());
        var payload = "new-backup-payload"u8.ToArray();

        await BackupExport.WriteAtomicAsync(path, payload);

        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public async Task Write_creates_new_file_without_temp_leftovers()
    {
        var dir = TestDirs.New("backup-atomic-new");
        var path = Path.Combine(dir, "BYDLO.bydlobackup");

        await BackupExport.WriteAtomicAsync(path, "fresh"u8.ToArray());

        Assert.Equal("fresh"u8.ToArray(), await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public async Task Missing_directory_fails_without_creating_anything()
    {
        var dir = TestDirs.New("backup-atomic-missing");
        var path = Path.Combine(dir, "no-such-dir", "BYDLO.bydlobackup");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => BackupExport.WriteAtomicAsync(path, "x"u8.ToArray()));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Destination_directory_as_target_fails_and_cleans_temp()
    {
        var dir = TestDirs.New("backup-atomic-dir");
        var before = Directory.GetFiles(dir);

        await Assert.ThrowsAnyAsync<Exception>(() => BackupExport.WriteAtomicAsync(dir, "x"u8.ToArray()));
        Assert.Equal(before, Directory.GetFiles(dir));
    }

    [Fact]
    public void ExportBackup_uses_atomic_write_and_never_truncates_first()
    {
        var root = FindRepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "SettingsViewModel.Backup.cs"));
        var start = text.IndexOf("private async Task ExportBackup()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = text.IndexOf("private async Task OpenBackup()", StringComparison.Ordinal);
        Assert.True(end > start);
        var body = text.Substring(start, end - start);
        Assert.Contains("WriteAtomicAsync", body, StringComparison.Ordinal);
        Assert.Contains("TryGetLocalPath", body, StringComparison.Ordinal);
        Assert.Contains("IsRemoteTargetMissingAsync", body, StringComparison.Ordinal);
        Assert.Contains("backup.export.exists", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SetLength(0)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remote_existing_target_is_never_opened_for_writing()
    {
        static Task<Stream> OpenRead() => Task.FromResult<Stream>(new MemoryStream("old-backup"u8.ToArray()));

        // The probe reports existing, so callers must not open the write stream at all.
        Assert.False(await BackupExport.IsRemoteTargetMissingAsync(OpenRead));
    }

    [Fact]
    public async Task Remote_unexpected_read_error_fails_closed()
    {
        static Task<Stream> DeniedRead() => Task.FromException<Stream>(new UnauthorizedAccessException());
        static Task<Stream> NetworkRead() => Task.FromException<Stream>(new IOException("Fake provider failure."));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => BackupExport.IsRemoteTargetMissingAsync(DeniedRead));
        await Assert.ThrowsAsync<IOException>(() => BackupExport.IsRemoteTargetMissingAsync(NetworkRead));
    }

    [Fact]
    public async Task Remote_missing_target_writes_fully()
    {
        static Task<Stream> OpenRead() => Task.FromException<Stream>(new FileNotFoundException());
        var written = new MemoryStream();
        Task<Stream> OpenWrite() => Task.FromResult<Stream>(written);
        var payload = "new-backup-payload"u8.ToArray();

        Assert.True(await BackupExport.IsRemoteTargetMissingAsync(OpenRead));
        await BackupExport.WriteRemoteAsync(OpenWrite, payload);
        Assert.Equal(payload, written.ToArray());
    }

    [Fact]
    public async Task Remote_mid_write_failure_is_reported()
    {
        static Task<Stream> OpenRead() => Task.FromException<Stream>(new FileNotFoundException());
        var stream = new FailAfterStream(failAfter: 4);
        Task<Stream> OpenWrite() => Task.FromResult<Stream>(stream);
        var payload = "new-backup-payload"u8.ToArray();

        Assert.True(await BackupExport.IsRemoteTargetMissingAsync(OpenRead));
        await Assert.ThrowsAsync<IOException>(() => BackupExport.WriteRemoteAsync(OpenWrite, payload));
        Assert.True(stream.Received < payload.Length);
    }

    private sealed class FailAfterStream(int failAfter) : MemoryStream
    {
        public int Received { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = Math.Max(0, failAfter - Received);
            var take = Math.Min(room, count);
            if (take > 0)
            {
                base.Write(buffer, offset, take);
                Received += take;
            }
            throw new IOException("Fake mid-write failure.");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
