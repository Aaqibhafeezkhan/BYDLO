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
    private IAppUpdateBackend? _backend;
    private bool _ready;
    private bool _busy;

    public async Task<bool> DownloadIfDueAsync(AppSettings settings, bool blocked, CancellationToken ct = default)
    {
        if (!settings.AutoUpdateApp || blocked || _busy) { return false; }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastAutomaticUpdateCheck is { } last && now - last < TimeSpan.FromHours(24)) { return false; }
        _busy = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            var backend = _backend ??= createBackend();
            if (!backend.IsInstalled) { return false; }
            settings.LastAutomaticUpdateCheck = now;
            var downloaded = await backend.DownloadLatestAsync(ct).ConfigureAwait(false);
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
