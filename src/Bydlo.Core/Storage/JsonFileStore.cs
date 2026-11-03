using System.Text.Json;
using System.Text.Json.Serialization;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Storage;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes atomically (temp file + move) so a crash never leaves a half-written file.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, value, Options, ct).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return default;
        }

        try
        {
            await using var fs = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection and start fresh.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return default;
        }
    }
}

#pragma warning disable CA1001 // lives for the whole app lifetime, like ProfileStore below
public sealed class SettingsStore(string path)
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        await JsonDefaults.ReadAsync<AppSettings>(path, ct).ConfigureAwait(false) ?? new AppSettings();

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        // Rapid preference changes must not concurrently write or move the same temporary file.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await JsonDefaults.WriteAtomicAsync(path, settings, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}

public sealed class Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Subscription URLs often embed tokens, so the URL itself is stored in the secret store.</summary>
    public DateTimeOffset? LastUpdated { get; set; }

    /// <summary>Traffic counters from the last <c>subscription-userinfo</c> header (bytes).</summary>
    public long? Upload { get; set; }

    public long? Download { get; set; }

    public long? Total { get; set; }

    public DateTimeOffset? Expires { get; set; }
}

internal sealed class ProfileDocument
{
    public int Version { get; set; } = 1;

    public List<Profile> Profiles { get; set; } = [];

    public List<Subscription> Subscriptions { get; set; } = [];

    public List<ProxyChain> Chains { get; set; } = [];
}

/// <summary>Profile list persisted as JSON; secrets are delegated to <see cref="ISecretStore"/>.</summary>
#pragma warning disable CA1001 // lives for the whole app lifetime
public sealed partial class ProfileStore(string path, ISecretStore secrets)
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProfileDocument _doc = new();

    public IReadOnlyList<Profile> Profiles => _doc.Profiles;

    public IReadOnlyList<Subscription> Subscriptions => _doc.Subscriptions;

    public ISecretStore Secrets => secrets;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        _doc = await JsonDefaults.ReadAsync<ProfileDocument>(path, ct).ConfigureAwait(false) ?? new ProfileDocument();
        _doc.Profiles ??= [];
        _doc.Subscriptions ??= [];
        _doc.Chains ??= [];
    }

    public async Task<IReadOnlyList<Profile>> AddAsync(IEnumerable<ImportedProfile> imported, Guid? subscriptionId = null, CancellationToken ct = default)
    {
        var added = new List<Profile>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var items = imported.ToList();
            if (items.Select(p => p.Profile.Id).Distinct().Count() != items.Count || items.Any(p => _doc.Profiles.Any(existing => existing.Id == p.Profile.Id)))
            { throw new InvalidDataException("New profiles must have unique IDs."); }
            var written = new List<string>();
            try
            {
                foreach (var item in items)
                {
                    item.Profile.SubscriptionId = subscriptionId ?? item.Profile.SubscriptionId;
                    written.Add(item.Profile.Id.ToString());
                    await secrets.SaveAsync(written[^1], item.Secrets, ct).ConfigureAwait(false);
                    added.Add(item.Profile);
                }
                var next = new ProfileDocument { Profiles = [.. _doc.Profiles, .. added], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                foreach (var id in written) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        return added;
    }

    public Task UpdateAsync(Profile profile, CancellationToken ct = default) =>
        UpdateAsync(profile, null, ct);

    public async Task UpdateAsync(Profile profile, ProfileSecrets? updatedSecrets, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = _doc.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index < 0)
            {
                return;
            }

            var next = new ProfileDocument { Profiles = [.. _doc.Profiles], Subscriptions = [.. _doc.Subscriptions], Chains = [.. _doc.Chains] };
            next.Profiles[index] = profile;
            var id = profile.Id.ToString();
            var previous = updatedSecrets is null ? null : await secrets.LoadAsync(id, ct).ConfigureAwait(false);
            try
            {
                if (updatedSecrets is not null)
                {
                    await secrets.SaveAsync(id, updatedSecrets, ct).ConfigureAwait(false);
                }
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                if (previous is not null)
                {
                    await secrets.SaveAsync(id, previous, CancellationToken.None).ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_doc.Profiles.RemoveAll(p => p.Id == id) > 0)
            {
                await secrets.DeleteAsync(id.ToString(), ct).ConfigureAwait(false);
                DetachFromChainsUnsafe(id);
                await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Subscription> AddSubscriptionAsync(string name, string url, CancellationToken ct = default)
    {
        var sub = new Subscription { Name = name };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _doc.Subscriptions.Add(sub);
            await secrets.SaveAsync(SubscriptionSecretId(sub.Id), new ProfileSecrets { Values = { ["url"] = url } }, ct).ConfigureAwait(false);
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return sub;
    }

    /// <summary>Saves a previewed subscription and its credentials together, without downloading it again.</summary>
    public async Task<Subscription> AddSubscriptionWithProfilesAsync(string name, string url, IReadOnlyList<ImportedProfile> profiles, Bydlo.Core.Import.SubscriptionMetadata? metadata = null, CancellationToken ct = default)
    {
        var sub = new Subscription
        {
            Name = name,
            LastUpdated = DateTimeOffset.UtcNow,
            Upload = metadata?.Upload,
            Download = metadata?.Download,
            Total = metadata?.Total,
            Expires = metadata?.Expires,
        };
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (profiles.Count == 0 || profiles.Select(p => p.Profile.Id).Distinct().Count() != profiles.Count ||
                profiles.Any(p => _doc.Profiles.Any(existing => existing.Id == p.Profile.Id)))
            { throw new InvalidDataException("A new subscription requires new profiles."); }
            var written = new List<string>();
            try
            {
                written.Add(SubscriptionSecretId(sub.Id));
                await secrets.SaveAsync(written[0], new ProfileSecrets { Values = { ["url"] = url } }, ct).ConfigureAwait(false);
                foreach (var item in profiles)
                {
                    written.Add(item.Profile.Id.ToString());
                    await secrets.SaveAsync(written[^1], item.Secrets, ct).ConfigureAwait(false);
                    item.Profile.SubscriptionId = sub.Id;
                }
                var next = new ProfileDocument { Profiles = [.. _doc.Profiles, .. profiles.Select(p => p.Profile)], Subscriptions = [.. _doc.Subscriptions, sub], Chains = [.. _doc.Chains] };
                await JsonDefaults.WriteAtomicAsync(path, next, ct).ConfigureAwait(false);
                _doc = next;
            }
            catch
            {
                foreach (var id in written) { await secrets.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false); }
                throw;
            }
        }
        finally { _gate.Release(); }
        return sub;
    }

    /// <summary>Replaces all profiles of a subscription with a fresh list.</summary>
    public async Task ReplaceSubscriptionProfilesAsync(Guid subscriptionId, IReadOnlyList<ImportedProfile> fresh, Bydlo.Core.Import.SubscriptionMetadata? metadata = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");

            foreach (var old in _doc.Profiles.Where(p => p.SubscriptionId == subscriptionId).ToList())
            {
                _doc.Profiles.Remove(old);
                await secrets.DeleteAsync(old.Id.ToString(), ct).ConfigureAwait(false);
                DetachFromChainsUnsafe(old.Id);
            }

            foreach (var item in fresh)
            {
                item.Profile.SubscriptionId = subscriptionId;
                _doc.Profiles.Add(item.Profile);
                await secrets.SaveAsync(item.Profile.Id.ToString(), item.Secrets, ct).ConfigureAwait(false);
            }

            sub.LastUpdated = DateTimeOffset.UtcNow;
            if (metadata is not null)
            {
                sub.Upload = metadata.Upload;
                sub.Download = metadata.Download;
                sub.Total = metadata.Total;
                sub.Expires = metadata.Expires;
            }

            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetSubscriptionUrlAsync(Guid subscriptionId, CancellationToken ct = default) =>
        (await secrets.LoadAsync(SubscriptionSecretId(subscriptionId), ct).ConfigureAwait(false))["url"];

    public async Task RenameSubscriptionAsync(Guid subscriptionId, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        { throw new ArgumentException("Subscription name must not be empty.", nameof(name)); }
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
                ?? throw new InvalidDataException("Subscription not found.");
            sub.Name = name.Trim();
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a subscription together with its profiles and all related secrets.</summary>
    public async Task RemoveSubscriptionAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var old in _doc.Profiles.Where(p => p.SubscriptionId == subscriptionId).ToList())
            {
                _doc.Profiles.Remove(old);
                await secrets.DeleteAsync(old.Id.ToString(), ct).ConfigureAwait(false);
            }
            _doc.Subscriptions.RemoveAll(s => s.Id == subscriptionId);
            await secrets.DeleteAsync(SubscriptionSecretId(subscriptionId), ct).ConfigureAwait(false);
            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string SubscriptionSecretId(Guid id) => $"subscription:{id}";
}
