using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;

namespace Bydlo.Core.Import;

/// <summary>Application-lifetime subscription scheduling, independent of page navigation.</summary>
public sealed class SubscriptionAutoRefresh(ProfileStore profiles, SubscriptionClient client, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _nextRetry = new();
    private readonly Dictionary<Guid, int> _failures = new();

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
        var now = _time.GetUtcNow();
        var due = profiles.Subscriptions
            .Where(s => s.LastUpdated is null || now - s.LastUpdated >= EffectiveInterval(s, settings))
            .Where(s => !_nextRetry.TryGetValue(s.Id, out var next) || now >= next)
            .ToList();
        var refreshed = 0;
        foreach (var sub in due)
        {
            var interval = EffectiveInterval(sub, settings);
            ct.ThrowIfCancellationRequested();
            try
            {
                var url = await profiles.GetSubscriptionUrlAsync(sub.Id, ct);
                if (url is null) { continue; }
                var preview = await client.FetchRefreshAsync(url, sub.UserAgent, ct);
                if (preview.Import.IsEmpty)
                {
                    RecordFailure(sub.Id, now, interval);
                    continue;
                }
                await profiles.ReplaceSubscriptionProfilesAsync(sub.Id, preview.Import.Profiles, preview.Metadata, ct);
                _nextRetry.Remove(sub.Id);
                _failures.Remove(sub.Id);
                refreshed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException
                or InvalidDataException or IOException or UnauthorizedAccessException or EngineException)
            {
                RecordFailure(sub.Id, now, interval);
                onError(ex);
            }
        }
        return refreshed;
    }

    /// <summary>Provider header interval wins when present, otherwise the global setting (both 1-168 h).</summary>
    internal static TimeSpan EffectiveInterval(Subscription sub, AppSettings settings) =>
        TimeSpan.FromHours(Math.Clamp(sub.UpdateIntervalHours ?? settings.SubscriptionUpdateIntervalHours, 1, 168));

    internal TimeSpan BackoffDelay(int failures, TimeSpan interval)
    {
        var minutes = Math.Pow(2, Math.Min(Math.Max(failures, 1), 10));
        return TimeSpan.FromMinutes(Math.Min(interval.TotalMinutes, minutes));
    }

    private void RecordFailure(Guid id, DateTimeOffset now, TimeSpan interval)
    {
        var failures = _failures.TryGetValue(id, out var count) ? count + 1 : 1;
        _failures[id] = failures;
        _nextRetry[id] = now + BackoffDelay(failures, interval);
    }
}
