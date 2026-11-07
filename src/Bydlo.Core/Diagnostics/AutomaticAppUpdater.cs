using Bydlo.Core.Models;

namespace Bydlo.Core.Diagnostics;

/// <summary>Package adapters perform I/O; the policy is independently testable without running an updater.</summary>
public interface IAppUpdateBackend
{
    bool IsInstalled { get; }
    Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct);
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

    /// <summary>
    /// Whether this copy can update itself at all. Archives, portable layouts and unpackaged builds
    /// answer false, and the UI must say so instead of leaving the opt-in switch on (issue #456).
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            return (_backend ??= createBackend()).IsInstalled;
        }
        catch (Exception)
        {
            // A capability probe must never break the settings page; any failure means "cannot update".
            return false;
        }
    }

    /// <summary>Scheduled download: at most once per 24 hours.</summary>
    public async Task<bool> DownloadIfDueAsync(AppSettings settings, bool blocked,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!settings.AutoUpdateApp || blocked || _busy) { return false; }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastAutomaticUpdateCheck is { } last && now - last < TimeSpan.FromHours(24)) { return false; }
        return await DownloadAsync(settings, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Explicit user request: ignores the daily window but keeps every other guard.</summary>
    public Task<bool> DownloadNowAsync(AppSettings settings, bool blocked,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (!settings.AutoUpdateApp || blocked || _busy) { return Task.FromResult(false); }
        return DownloadAsync(settings, progress, ct);
    }

    private async Task<bool> DownloadAsync(AppSettings settings, IProgress<int>? progress, CancellationToken ct)
    {
        _busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            var backend = _backend ??= createBackend();
            if (!backend.IsInstalled) { return false; }
            settings.LastAutomaticUpdateCheck = (timeProvider ?? TimeProvider.System).GetUtcNow();
            bool downloaded;
            try
            {
                downloaded = await backend.DownloadLatestAsync(progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing the window mid-download must not consume the daily window (issue #456):
                // the user saw nothing happening, quit after 27 seconds and waited a day.
                settings.LastAutomaticUpdateCheck = null;
                throw;
            }
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
