using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class SystemSecretStoreTests
{
    private const string Id = "profile";
    private static ProfileSecrets Credentials => new() { Values = { [Keys.Password] = "fake-password" } };

    [Fact]
    public async Task Migrates_the_existing_file_only_after_the_system_write_succeeds()
    {
        var fallback = new InMemorySecretStore();
        await fallback.SaveAsync(Id, Credentials);
        var backend = new FakeBackend { Available = false };
        var store = new MigratingSecretStore(backend, fallback);

        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        Assert.True(store.UsingFallback);
        Assert.Equal("fake-password", (await fallback.LoadAsync(Id))[Keys.Password]);

        backend.Available = true;
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        Assert.False(store.UsingFallback);
        Assert.Null((await fallback.LoadAsync(Id))[Keys.Password]);
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
    }

    [Fact]
    public async Task A_locked_system_store_cannot_overwrite_migrated_data_with_empty_credentials()
    {
        var backend = new FakeBackend();
        var store = new MigratingSecretStore(backend, new InMemorySecretStore());
        await store.SaveAsync(Id, Credentials);

        backend.Available = false;
        var error = await Assert.ThrowsAsync<EngineException>(() => store.LoadAsync(Id));
        Assert.Equal("settings.secrets.locked", error.LocalizationKey);
        await Assert.ThrowsAsync<EngineException>(() => store.SaveAsync(Id, ProfileSecrets.Empty));

        backend.Available = true;
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
    }

    [Fact]
    public async Task Deleted_items_do_not_reappear_when_the_system_store_returns()
    {
        var backend = new FakeBackend();
        var store = new MigratingSecretStore(backend, new InMemorySecretStore());
        await store.SaveAsync(Id, Credentials);
        backend.Available = false;
        await store.DeleteAsync(Id);
        Assert.Empty((await store.LoadAsync(Id)).Values);
        backend.Available = true;
        Assert.Empty((await store.LoadAsync(Id)).Values);
        Assert.Null(await backend.ReadAsync(Id, default));
    }

    private sealed class FakeBackend : ISystemSecretBackend
    {
        private readonly Dictionary<string, string> _items = [];
        public bool Available { get; set; } = true;
        public string Description => "Fake system vault";

        public Task<string?> ReadAsync(string id, CancellationToken ct)
        {
            Check();
            return Task.FromResult(_items.GetValueOrDefault(id));
        }

        public Task WriteAsync(string id, string value, CancellationToken ct)
        {
            Check();
            _items[id] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string id, CancellationToken ct)
        {
            Check();
            _items.Remove(id);
            return Task.CompletedTask;
        }

        private void Check()
        {
            if (!Available) { throw new SystemSecretUnavailableException(); }
        }
    }
}
