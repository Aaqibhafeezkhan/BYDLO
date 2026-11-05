namespace Bydlo.Core.Backup;

/// <summary>
/// Atomic file replacement for backup export (issue #334).
/// The previous implementation truncated the destination before writing, so an
/// interrupted write destroyed the last good backup. This helper fully writes
/// and flushes a temporary file in the same directory, then atomically moves it
/// over the destination. The old file is untouched until the move succeeds.
/// </summary>
public static class BackupExport
{
    /// <summary>
    /// Writes <paramref name="payload"/> to a temp file next to <paramref name="destinationPath"/>,
    /// flushes it to disk and atomically replaces the destination. Cleans up the temp file on failure.
    /// </summary>
    public static async Task WriteAtomicAsync(string destinationPath, byte[] payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(payload);
        var full = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(full) ?? throw new ArgumentException("Destination has no directory.", nameof(destinationPath));
        var temp = Path.Combine(directory, "." + Path.GetFileName(full) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch { }
            throw;
        }
    }

    /// <summary>
    /// Probes whether a non-local backup target already exists without opening it
    /// for writing (issue #334). Some providers truncate on open, so even probing
    /// with a write stream could destroy the previous backup.
    /// Fail-closed: only a confirmed not-found counts as missing; any other read
    /// error is rethrown so the export aborts instead of risking an overwrite.
    /// </summary>
    /// <returns>False when the target exists and must be kept; true when missing.</returns>
    public static async Task<bool> IsRemoteTargetMissingAsync(Func<Task<Stream>> openRead, CancellationToken ct = default)
    {
        try
        {
            await using var probe = await openRead().ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    /// <summary>
    /// Writes a new backup to a non-local target previously confirmed missing.
    /// Without an atomic replacement primitive a mid-write failure can still leave
    /// a partial file, which is why existing targets are never overwritten.
    /// </summary>
    public static async Task WriteRemoteAsync(Func<Task<Stream>> openWrite, byte[] payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await using var stream = await openWrite().ConfigureAwait(false);
        await stream.WriteAsync(payload.AsMemory(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        if (stream.CanSeek && stream.Length != payload.Length)
        {
            stream.SetLength(payload.Length);
        }
    }
}
