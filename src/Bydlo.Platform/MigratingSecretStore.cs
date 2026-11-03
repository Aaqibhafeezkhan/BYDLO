using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

internal interface ISystemSecretBackend
{
    string Description { get; }
    Task<string?> ReadAsync(string id, CancellationToken ct);
    Task WriteAsync(string id, string value, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

internal sealed class SystemSecretUnavailableException : Exception;

#pragma warning disable CA1001 // The gate lives for the duration of the application's secret store.
internal sealed class MigratingSecretStore(ISystemSecretBackend system, ISecretStore fallback) : ISecretStore
#pragma warning restore CA1001
{
    private const string DeletedKey = "\0bydlo-deleted";
    private const string RemoteKey = "\0bydlo-system";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _usingFallback;

    public bool UsingFallback => _usingFallback;

    public string ProtectionDescription => _usingFallback ? fallback.ProtectionDescription : system.Description;

    public async Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var file = await fallback.LoadAsync(id, ct).ConfigureAwait(false);
            try
            {
                if (file.Values.ContainsKey(DeletedKey))
                {
                    await system.DeleteAsync(id, ct).ConfigureAwait(false);
                    await fallback.DeleteAsync(id, ct).ConfigureAwait(false);
                    _usingFallback = false;
                    return ProfileSecrets.Empty;
                }
                if (file.Values.Count != 0 && !file.Values.ContainsKey(RemoteKey))
                {
                    await system.WriteAsync(id, JsonSerializer.Serialize(file.Values), ct).ConfigureAwait(false);
                    await MarkRemoteAsync(id, ct).ConfigureAwait(false);
                    _usingFallback = false;
                    return file;
                }
                var value = await system.ReadAsync(id, ct).ConfigureAwait(false);
                _usingFallback = false;
                return value is null ? ProfileSecrets.Empty : new ProfileSecrets
                {
                    Values = new(JsonSerializer.Deserialize<Dictionary<string, string>>(value) ?? [], StringComparer.OrdinalIgnoreCase),
                };
            }
            catch (SystemSecretUnavailableException)
            {
                _usingFallback = true;
                if (file.Values.ContainsKey(RemoteKey))
                {
                    throw new Bydlo.Core.Engines.EngineException("settings.secrets.locked", localizationKey: "settings.secrets.locked");
                }
                return file.Values.ContainsKey(DeletedKey) ? ProfileSecrets.Empty : file;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await system.WriteAsync(id, JsonSerializer.Serialize(secrets.Values), ct).ConfigureAwait(false);
                await MarkRemoteAsync(id, ct).ConfigureAwait(false);
                _usingFallback = false;
            }
            catch (SystemSecretUnavailableException)
            {
                _usingFallback = true;
                var previous = await fallback.LoadAsync(id, ct).ConfigureAwait(false);
                if (previous.Values.ContainsKey(RemoteKey))
                {
                    throw new Bydlo.Core.Engines.EngineException("settings.secrets.locked", localizationKey: "settings.secrets.locked");
                }
                await fallback.SaveAsync(id, secrets, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await system.DeleteAsync(id, ct).ConfigureAwait(false);
                await fallback.DeleteAsync(id, ct).ConfigureAwait(false);
                _usingFallback = false;
            }
            catch (SystemSecretUnavailableException)
            {
                _usingFallback = true;
                await fallback.SaveAsync(id, new ProfileSecrets { Values = { [DeletedKey] = "true" } }, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
    private Task MarkRemoteAsync(string id, CancellationToken ct) =>
        fallback.SaveAsync(id, new ProfileSecrets { Values = { [RemoteKey] = "true" } }, ct);
}
