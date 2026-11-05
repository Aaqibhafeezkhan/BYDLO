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
}
