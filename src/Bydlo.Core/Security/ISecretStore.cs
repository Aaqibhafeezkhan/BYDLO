using Bydlo.Core.Models;

namespace Bydlo.Core.Security;

/// <summary>Platform-protected storage for credentials (DPAPI on Windows, encrypted file elsewhere).</summary>
public interface ISecretStore
{
    /// <summary>Human readable description of the protection, shown in Settings → Advanced.</summary>
    string ProtectionDescription { get; }

    Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default);

    Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
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
