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

public sealed class SettingsStore(string path)
{
    public async Task<AppSettings> LoadAsync(CancellationToken ct = default) =>
        await JsonDefaults.ReadAsync<AppSettings>(path, ct).ConfigureAwait(false) ?? new AppSettings();

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => JsonDefaults.WriteAtomicAsync(path, settings, ct);
}

public sealed class Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Subscription URLs often embed tokens, so the URL itself is stored in the secret store.</summary>
    public DateTimeOffset? LastUpdated { get; set; }
}

internal sealed class ProfileDocument
{
    public int Version { get; set; } = 1;

    public List<Profile> Profiles { get; set; } = [];

    public List<Subscription> Subscriptions { get; set; } = [];
}

/// <summary>Profile list persisted as JSON; secrets are delegated to <see cref="ISecretStore"/>.</summary>
#pragma warning disable CA1001 // lives for the whole app lifetime
public sealed class ProfileStore(string path, ISecretStore secrets)
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
    }

    public async Task<IReadOnlyList<Profile>> AddAsync(IEnumerable<ImportedProfile> imported, Guid? subscriptionId = null, CancellationToken ct = default)
    {
        var added = new List<Profile>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var item in imported)
            {
                item.Profile.SubscriptionId = subscriptionId ?? item.Profile.SubscriptionId;
                _doc.Profiles.Add(item.Profile);
                await secrets.SaveAsync(item.Profile.Id.ToString(), item.Secrets, ct).ConfigureAwait(false);
                added.Add(item.Profile);
            }

            await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        return added;
    }

    public async Task UpdateAsync(Profile profile, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var i = _doc.Profiles.FindIndex(p => p.Id == profile.Id);
            if (i >= 0)
            {
                _doc.Profiles[i] = profile;
                await JsonDefaults.WriteAtomicAsync(path, _doc, ct).ConfigureAwait(false);
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

    /// <summary>Replaces all profiles of a subscription with a fresh list.</summary>
    public async Task ReplaceSubscriptionProfilesAsync(Guid subscriptionId, IReadOnlyList<ImportedProfile> fresh, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var old in _doc.Profiles.Where(p => p.SubscriptionId == subscriptionId).ToList())
            {
                _doc.Profiles.Remove(old);
                await secrets.DeleteAsync(old.Id.ToString(), ct).ConfigureAwait(false);
            }

            foreach (var item in fresh)
            {
                item.Profile.SubscriptionId = subscriptionId;
                _doc.Profiles.Add(item.Profile);
                await secrets.SaveAsync(item.Profile.Id.ToString(), item.Secrets, ct).ConfigureAwait(false);
            }

            var sub = _doc.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (sub is not null)
            {
                sub.LastUpdated = DateTimeOffset.UtcNow;
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

    private static string SubscriptionSecretId(Guid id) => $"subscription:{id}";
}
