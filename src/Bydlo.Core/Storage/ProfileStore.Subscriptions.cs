using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Core.Storage;

public sealed partial class ProfileStore
{
    public async Task ReplaceSubscriptionProfilesAsync(Guid subscriptionId, IReadOnlyList<ImportedProfile> fresh,
        SubscriptionMetadata? metadata = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");
            var existingIds = _doc.Profiles.Select(profile => profile.Id).ToHashSet();
            if (fresh.Count == 0 || fresh.Any(item => !existingIds.Add(item.Profile.Id)))
            {
                throw new InvalidDataException("A subscription refresh requires nonempty profiles with new, unique IDs.");
            }

            var previous = _doc.Profiles.Where(profile => profile.SubscriptionId == subscriptionId).ToArray();
            var incoming = fresh.GroupBy(item => NodeIdentity(item.Profile, item.Secrets)).ToDictionary(group => group.Key, group => group.ToList());
            var endpoints = fresh.Select(item => EndpointIdentity(item.Profile)).ToHashSet();
            var candidates = new List<(Profile Profile, string Identity)>();
            foreach (var profile in previous.Where(profile => endpoints.Contains(EndpointIdentity(profile))))
            {
                var saved = await secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false);
                candidates.Add((profile, NodeIdentity(profile, saved)));
            }

            var matched = new Dictionary<Guid, ImportedProfile>();
            foreach (var group in candidates.GroupBy(candidate => candidate.Identity))
            {
                if (!incoming.TryGetValue(group.Key, out var remaining)) { continue; }
                var old = group.Select(candidate => candidate.Profile).ToList();

                // Match exact transports first when multiple nodes share credentials and endpoint.
                foreach (var profile in old.ToArray())
                {
                    var options = ConnectionOptions(sub.SourceOptions?.GetValueOrDefault(profile.Id) ?? profile.Options);
                    var index = remaining.FindIndex(item => ConnectionOptions(item.Profile.Options) == options);
                    if (index < 0) { continue; }
                    matched.Add(profile.Id, remaining[index]);
                    old.Remove(profile);
                    remaining.RemoveAt(index);
                }

                if (old.Count == 1 && remaining.Count == 1)
                {
                    matched.Add(old[0].Id, remaining[0]);
                    remaining.Clear();
                }
            }

            // Dropping or substituting an unavailable hop would silently change chain routing.
            var retained = _doc.Chains.SelectMany(chain => chain.ProfileIds).Concat(matched.Keys).ToHashSet();
            var removed = previous.Where(profile => !retained.Contains(profile.Id) && !profile.IsFavorite).ToArray();
            var removedIds = removed.Select(profile => profile.Id).ToHashSet();
            // Selected-only subscriptions keep the user's preview choice: refresh updates
            // already saved nodes but never re-adds deselected or brand-new provider nodes.
            var added = sub.Mode == SubscriptionMode.SelectedOnly
                ? Array.Empty<ImportedProfile>()
                : incoming.Values.SelectMany(group => group).ToArray();
            var sourceOptions = new Dictionary<Guid, Dictionary<string, string>>();
            foreach (var profile in previous.Where(profile => !removedIds.Contains(profile.Id)))
            {
                if (matched.TryGetValue(profile.Id, out var match))
                {
                    sourceOptions.Add(profile.Id, new(match.Profile.Options, StringComparer.OrdinalIgnoreCase));
                }
                else if (sub.SourceOptions?.GetValueOrDefault(profile.Id) is { } baseline)
                {
                    sourceOptions.Add(profile.Id, baseline);
                }
            }
            foreach (var item in added)
            {
                sourceOptions.Add(item.Profile.Id, new(item.Profile.Options, StringComparer.OrdinalIgnoreCase));
            }

            var updated = new Subscription
            {
                Id = sub.Id, Name = sub.Name, LastUpdated = (time ?? TimeProvider.System).GetUtcNow(), SourceOptions = sourceOptions,
                Mode = sub.Mode,
                Upload = metadata is null ? sub.Upload : metadata.Upload,
                Download = metadata is null ? sub.Download : metadata.Download,
                Total = metadata is null ? sub.Total : metadata.Total,
                Expires = metadata is null ? sub.Expires : metadata.Expires,
            };
            var next = new ProfileDocument
            {
                Version = _doc.Version,
                PendingSecretDeletes = [.. _doc.PendingSecretDeletes],
                Profiles = _doc.Profiles.Where(profile => !removedIds.Contains(profile.Id))
                    .Select(profile => matched.TryGetValue(profile.Id, out var match)
                        ? CopySubscriptionProfile(profile, subscriptionId, MergeOptions(profile, match.Profile, sub))
                        : profile).ToList(),
                Subscriptions = _doc.Subscriptions.Select(item => item.Id == subscriptionId ? updated : item).ToList(),
                Chains = [.. _doc.Chains],
            };

            var staged = new List<Guid>();
            try
            {
                foreach (var item in added)
                {
                    ct.ThrowIfCancellationRequested();
                    var profile = item.Profile;
                    staged.Add(profile.Id);
                    await secrets.SaveAsync(profile.Id.ToString(), item.Secrets, ct).ConfigureAwait(false);
                    next.Profiles.Add(CopySubscriptionProfile(profile, subscriptionId, profile.Options));
                }
                ct.ThrowIfCancellationRequested();
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                await DeleteUnreferencedSecretsAsync(staged.Select(id => id.ToString())).ConfigureAwait(false);
                throw;
            }

            await DeleteUnreferencedSecretsAsync(removedIds.Select(id => id.ToString())).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static (ProtocolKind Protocol, string Host, int Port) EndpointIdentity(Profile profile) =>
        (profile.Protocol, profile.Host.ToUpperInvariant(), profile.Port);

    private static string NodeIdentity(Profile profile, ProfileSecrets secrets) => JsonSerializer.Serialize(new
    {
        profile.Protocol,
        Host = profile.Host.ToUpperInvariant(),
        profile.Port,
        Credentials = CanonicalValues(secrets.Values),
    });

    private static Profile CopySubscriptionProfile(Profile profile, Guid subscriptionId, Dictionary<string, string> options) =>
        new()
        {
            Id = profile.Id, Name = profile.Name, Protocol = profile.Protocol, Host = profile.Host, Port = profile.Port,
            Options = new(options, StringComparer.OrdinalIgnoreCase), SubscriptionId = subscriptionId,
            PreferredEngine = profile.PreferredEngine, AddedAt = profile.AddedAt, IsFavorite = profile.IsFavorite,
        };

    private static Dictionary<string, string> MergeOptions(Profile saved, Profile incoming, Subscription subscription)
    {
        if (subscription.SourceOptions?.GetValueOrDefault(saved.Id) is not { } baseline)
        {
            // Legacy subscriptions have no baseline. Keep their existing options on the first refresh.
            return saved.Options;
        }

        var previous = new Dictionary<string, string>(baseline, StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, string>(incoming.Options, StringComparer.OrdinalIgnoreCase);
        foreach (var key in previous.Keys.Union(saved.Options.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (saved.Options.GetValueOrDefault(key) == previous.GetValueOrDefault(key)) { continue; }
            if (saved.Options.TryGetValue(key, out var value)) { merged[key] = value; }
            else { merged.Remove(key); }
        }
        return merged;
    }

    private static string ConnectionOptions(Dictionary<string, string> options) => JsonSerializer.Serialize(CanonicalValues(options));

    private static IEnumerable<KeyValuePair<string, string>> CanonicalValues(Dictionary<string, string> values) =>
        values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => KeyValuePair.Create(pair.Key.ToUpperInvariant(), pair.Value));

    private async Task DeleteUnreferencedSecretsAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EngineException)
            {
                // An unreferenced secret is safer than rolling back an already committed document.
            }
        }
    }
}
