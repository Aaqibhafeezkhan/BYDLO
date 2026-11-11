using Bydlo.Core.Models;

namespace Bydlo.Core.Diagnostics;

/// <summary>Startup policy and cached notification. HTTP can be replaced for isolated tests.</summary>
public sealed class StartupUpdateChecker(GitHubUpdateClient client, TimeProvider? timeProvider = null)
{
    public async Task<GitHubRelease?> CheckAsync(AppSettings settings, string currentVersion,
        bool networkBlocked, CancellationToken ct = default)
    {
        if (settings.PendingUpdate is { } pending && !GitHubUpdateClient.IsNewer(currentVersion, pending.Tag))
        {
            settings.PendingUpdate = null;
        }
        if (!FeatureConsent.From(settings).Allows(OptionalFeature.AutomaticUpdates) ||
            !settings.CheckForUpdates || networkBlocked)
        {
            return null;
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (settings.LastUpdateCheck is { } last && now - last < TimeSpan.FromHours(24))
        {
            return settings.PendingUpdate;
        }
        // Failed attempts also respect the interval, avoiding a request on every offline startup.
        settings.LastUpdateCheck = now;
        try
        {
            settings.PendingUpdate = await client.CheckBydloAsync(currentVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // Startup continues; a previously discovered release remains available offline.
        }
        return settings.PendingUpdate;
    }
}
