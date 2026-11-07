using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using Velopack;
using Velopack.Sources;

namespace Bydlo.App.Services;

internal sealed class PackagedAppUpdater(string dataDirectory) : IAppUpdateBackend
{
    private readonly UpdateManager _manager = new(new GithubSource("https://github.com/endercodezz/BYDLO", null, false));

    public bool IsInstalled => !File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt")) &&
        AutomaticAppUpdater.HasSeparateDataDirectory(AppContext.BaseDirectory, dataDirectory) && _manager.IsInstalled;

    public async Task<bool> DownloadLatestAsync(IProgress<int>? progress, CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("download application update");
        var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
        if (update is not null)
        {
            Action<int>? report = progress is null ? null : progress.Report;
            await _manager.DownloadUpdatesAsync(update, report, ct).ConfigureAwait(false);
        }
        return _manager.UpdatePendingRestart is not null;
    }

    public void ApplyOnExit()
    {
        NetworkSafetyGuard.EnsureAllowed("install application update");
        _manager.WaitExitThenApplyUpdates(_manager.UpdatePendingRestart, silent: true, restart: false);
    }
}
