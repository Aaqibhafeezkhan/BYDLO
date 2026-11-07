using Bydlo.Core.Models;

namespace Bydlo.Core.Diagnostics;

/// <summary>Package adapters perform I/O; the policy is independently testable without running an updater.</summary>
public interface IAppUpdateBackend
{
    bool IsInstalled { get; }
    Task<bool> DownloadLatestAsync(CancellationToken ct);
    void ApplyOnExit();
}

public sealed class AutomaticAppUpdater(Func<IAppUpdateBackend> createBackend, TimeProvider? timeProvider = null)
{
    public static bool HasSeparateDataDirectory(string applicationDirectory, string dataDirectory)
    {
        var application = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return !data.Equals(application, comparison) &&
            !data.StartsWith(application + Path.DirectorySeparatorChar, comparison);
    }

    private IAppUpdateBackend? _backend;
    private bool _ready;
    private bool _busy;
    private DateTimeOffset? _lastEmptyCheck;

    public async Task<bool> DownloadIfDueAsync(AppSettings settings, bool blocked, CancellationToken ct = default)
    {
        if (!settings.AutoUpdateApp || blocked || _busy) { return false; }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastAutomaticUpdateCheck is { } last && now - last < TimeSpan.FromHours(24)) { return false; }
        // An empty check only rate-limits re-polling of the feed; a release published soon after is picked up within the hour.
        if (_lastEmptyCheck is { } empty && now - empty < TimeSpan.FromHours(1)) { return false; }
        _busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            var backend = _backend ??= createBackend();
            if (!backend.IsInstalled) { return false; }
            bool downloaded;
            try { downloaded = await backend.DownloadLatestAsync(ct).ConfigureAwait(false); }
            catch when (!ct.IsCancellationRequested)
            {
                // A failed check is rate-limited like an empty one, so an offline feed is not re-polled on every call.
                _lastEmptyCheck = now;
                throw;
            }
            // Only a successful download starts the 24h lock.
            if (downloaded) { settings.LastAutomaticUpdateCheck = now; }
            else { _lastEmptyCheck = now; }
            _ready = downloaded && settings.AutoUpdateApp && !ct.IsCancellationRequested;
            return _ready;
        }
        finally { _busy = false; }
    }

    public void ApplyOnExit(AppSettings settings, bool blocked)
    {
        if (!settings.AutoUpdateApp || blocked || !_ready || _busy) { return; }
        _backend!.ApplyOnExit();
        _ready = false;
    }
}
