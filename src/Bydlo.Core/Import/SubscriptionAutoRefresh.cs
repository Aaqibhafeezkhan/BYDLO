using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Import;

/// <summary>Application-lifetime subscription scheduling, independent of page navigation.</summary>
public sealed class SubscriptionAutoRefresh(ProfileStore profiles, SubscriptionClient client, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The caller dispatches refresh work to its owning UI context. Passes never overlap.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> refresh, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        try
        {
            ct.ThrowIfCancellationRequested();
            await refresh(ct).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await refresh(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async Task<int> RefreshIfDueAsync(AppSettings settings, bool blocked, Action<Exception> onError,
        CancellationToken ct = default)
    {
        if (!settings.SubscriptionAutoUpdate || blocked || ct.IsCancellationRequested) { return 0; }
        var interval = TimeSpan.FromHours(Math.Clamp(settings.SubscriptionUpdateIntervalHours, 1, 168));
        var now = _time.GetUtcNow();
        var due = profiles.Subscriptions.Where(s => s.LastUpdated is null || now - s.LastUpdated >= interval).ToList();
        var refreshed = 0;
        foreach (var sub in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = await profiles.GetSubscriptionUrlAsync(sub.Id, ct);
                if (url is null) { continue; }
                var preview = await client.FetchRefreshAsync(url, ct);
                if (preview.Import.IsEmpty) { continue; }
                await profiles.ReplaceSubscriptionProfilesAsync(sub.Id, preview.Import.Profiles, preview.Metadata, ct);
                refreshed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                or InvalidDataException or IOException or UnauthorizedAccessException or EngineException)
            {
                onError(ex);
            }
        }
        return refreshed;
    }
}
