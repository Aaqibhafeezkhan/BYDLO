using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Platform;
using Bydlo.Core.Backup;
using Bydlo.Core.Platform;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class SystemSecretStoreTests
{
    private const string Id = "profile";
    private static ProfileSecrets Credentials => new() { Values = { [Keys.Password] = "fake-password" } };

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public async Task Factory_selects_system_or_file_storage_without_opening_a_real_vault(
        bool isolated, bool available, bool expectedSystem)
    {
        var paths = new RootedAppPaths(TestDirs.New("vault-selection"));
        var created = false;
        var backend = new FakeBackend();
        var store = SecretStoreFactory.CreatePortable(paths, isolated, _ =>
        {
            created = true;
            return available ? backend : null;
        });
        Assert.Equal(!isolated, created);
        Assert.Equal(expectedSystem, store is MigratingSecretStore);
        await store.SaveAsync(Id, Credentials);
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        Assert.Equal(expectedSystem, await backend.ReadAsync(Id, default) is not null);
    }

    [Fact]
    public void Separate_data_directories_use_distinct_system_vault_scopes()
    {
        var scopes = new List<string>();
        var root = TestDirs.New("vault-scope");
        foreach (var folder in new[] { root, root, Path.Combine(root, "other") })
        {
            _ = SecretStoreFactory.CreatePortable(new RootedAppPaths(folder), false, scope =>
            {
                scopes.Add(scope);
                return new FakeBackend();
            });
        }
        Assert.Equal(scopes[0], scopes[1]);
        Assert.NotEqual(scopes[0], scopes[2]);
    }

    [Fact]
    public async Task Existing_encrypted_files_survive_upgrade_and_store_recreation()
    {
        var directory = TestDirs.New("vault-upgrade");
        await new AesFileSecretStore(directory).SaveAsync(Id, Credentials);
        var backend = new FakeBackend();
        var store = new MigratingSecretStore(backend, new AesFileSecretStore(directory));
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        var reopened = new MigratingSecretStore(backend, new AesFileSecretStore(directory));
        Assert.Equal("fake-password", (await reopened.LoadAsync(Id))[Keys.Password]);
        Assert.Null((await new AesFileSecretStore(directory).LoadAsync(Id))[Keys.Password]);
        backend.Available = false;
        await Assert.ThrowsAsync<EngineException>(() => reopened.LoadAsync(Id));
    }

    [Fact]
    public async Task Migration_retains_file_credentials_until_system_readback_matches()
    {
        var fallback = new InMemorySecretStore();
        await fallback.SaveAsync(Id, Credentials);
        var backend = new FakeBackend { DiscardWrites = true };
        var store = new MigratingSecretStore(backend, fallback);
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        Assert.True(SecretStoreFactory.IsUsingSystemFallback(store));
        Assert.Equal("fake-password", (await fallback.LoadAsync(Id))[Keys.Password]);
        backend.DiscardWrites = false;
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
        Assert.False(store.UsingFallback);
    }

    [Fact]
    public async Task Reading_an_unrelated_item_does_not_hide_a_fallback_warning()
    {
        var backend = new FakeBackend { Available = false };
        var store = new MigratingSecretStore(backend, new InMemorySecretStore());
        await store.SaveAsync(Id, Credentials);
        backend.Available = true;
        await store.LoadAsync("other");
        Assert.True(store.UsingFallback);
        await store.LoadAsync(Id);
        Assert.False(store.UsingFallback);
    }

    [Fact]
    public async Task Missing_migrated_credentials_report_an_error_instead_of_an_empty_secret()
    {
        var backend = new FakeBackend();
        var store = new MigratingSecretStore(backend, new InMemorySecretStore());
        await store.SaveAsync(Id, Credentials);
        await backend.DeleteAsync(Id, default);
        var error = await Assert.ThrowsAsync<EngineException>(() => store.LoadAsync(Id));
        Assert.Equal("settings.secrets.missing", error.LocalizationKey);
        await store.SaveAsync(Id, Credentials);
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
    }

    [Fact]
    public async Task Cancelled_migration_keeps_the_original_file_and_can_be_retried()
    {
        var fallback = new InMemorySecretStore();
        await fallback.SaveAsync(Id, Credentials);
        var backend = new FakeBackend { CancelRead = true };
        var store = new MigratingSecretStore(backend, fallback);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(Id));
        Assert.Equal("fake-password", (await fallback.LoadAsync(Id))[Keys.Password]);
        backend.CancelRead = false;
        Assert.Equal("fake-password", (await store.LoadAsync(Id))[Keys.Password]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Backup_roundtrips_profile_subscription_and_tool_secrets(bool available)
    {
        var backend = new FakeBackend { Available = available };
        var secrets = new MigratingSecretStore(backend, new InMemorySecretStore());
        var profiles = new ProfileStore(Path.Combine(TestDirs.New("vault-backup"), "profiles.json"), secrets);
        var subscription = new Subscription { Name = "Test subscription" };
        var data = new BackupData
        {
            Profiles = [new(new Profile { Name = "Test", Protocol = ProtocolKind.Trojan, Host = "example.test", Port = 443, SubscriptionId = subscription.Id }, Credentials)],
            Subscriptions = [new(subscription, "https://example.test/private-token")],
            ToolSecrets = new ProfileSecrets { Values = { ["secret"] = "fake-tool-secret", ["localproxy-password"] = "fake-lan-password" } },
        };
        await profiles.RestoreBackupAsync(data, BackupImportMode.Replace);
        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        var archive = BackupArchive.Encrypt(snapshot, "fake-backup-password");
        var restored = BackupArchive.Decrypt(archive, "fake-backup-password");
        var target = new ProfileStore(Path.Combine(TestDirs.New("vault-restore"), "profiles.json"),
            new MigratingSecretStore(new FakeBackend(), new InMemorySecretStore()));
        await target.RestoreBackupAsync(restored, BackupImportMode.Replace);
        var result = await target.CaptureBackupAsync(new AppSettings());
        Assert.Equal("fake-password", Assert.Single(result.Profiles).Secrets[Keys.Password]);
        Assert.Equal(data.Subscriptions[0].Url, Assert.Single(result.Subscriptions).Url);
        Assert.Equal("fake-tool-secret", result.ToolSecrets["secret"]);
        Assert.Equal("fake-lan-password", result.ToolSecrets["localproxy-password"]);

        backend.Available = true;
        await profiles.CaptureBackupAsync(new AppSettings());
        backend.Available = false;
        await Assert.ThrowsAsync<EngineException>(() => profiles.CaptureBackupAsync(new AppSettings()));
    }

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
        public bool DiscardWrites { get; set; }
        public bool CancelRead { get; set; }
        public string Description => "Fake system vault";

        public Task<string?> ReadAsync(string id, CancellationToken ct)
        {
            Check();
            if (CancelRead) { throw new OperationCanceledException(); }
            return Task.FromResult(_items.GetValueOrDefault(id));
        }

        public Task WriteAsync(string id, string value, CancellationToken ct)
        {
            Check();
            if (!DiscardWrites) { _items[id] = value; }
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
