using Bydlo.Core.Models;

namespace Bydlo.Core.Security;

/// <summary>Platform-protected credentials (DPAPI, Keychain, Secret Service or encrypted-file fallback).</summary>
public interface ISecretStore
{
    /// <summary>Human readable description of the protection, shown in Settings → Advanced.</summary>
    string ProtectionDescription { get; }

    Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default);

    /// <exception cref="SecretAccessException">The secret file exists but cannot be decrypted or parsed.</exception>
    Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
}

/// <summary>Stored credentials exist but cannot be decrypted, read or parsed (issue #344).</summary>
public sealed class SecretAccessException(string secretId, Exception? inner = null)
    : Engines.EngineException($"Backup secret '{secretId}' is missing or unreadable.", inner,
        localizationKey: "backup.secret.failed", localizationArguments: [secretId])
{
    public string SecretId { get; } = secretId;
}

/// <summary>Non-persistent store for tests and Mock Mode previews.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, ProfileSecrets> _items = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string ProtectionDescription => "In-memory (not persisted)";

    public Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _items[id] = new ProfileSecrets { Values = new(secrets.Values, StringComparer.OrdinalIgnoreCase) };
        }

        return Task.CompletedTask;
    }

    public Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(id, out var s)
                ? new ProfileSecrets { Values = new(s.Values, StringComparer.OrdinalIgnoreCase) }
                : ProfileSecrets.Empty);
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _items.Remove(id);
        }

        return Task.CompletedTask;
    }
}
