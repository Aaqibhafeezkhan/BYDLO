using System.Security.Cryptography;
using System.Text;
using Bydlo.Core.Backup;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class BackupTests
{
    private const string Password = "a long backup password 🔑";

    [Theory]
    [InlineData(ThemePreference.Custom, ThemePreference.System)]
    [InlineData(ThemePreference.Purple, ThemePreference.System)]
    [InlineData(ThemePreference.Oled, ThemePreference.Oled)]
    public void Restored_theme_without_local_custom_file_follows_system(ThemePreference stored, ThemePreference expected)
    {
        var data = Data();
        data.Settings.Theme = stored;
        data.Settings.CustomThemeId = "from-backup";
        var settings = BackupArchive.ImportSettings(data, new AppSettings(), BackupImportMode.Replace);
        Assert.Equal(expected, settings.Theme);
    }

    private static BackupData Data()
    {
        var sub = new Subscription { Name = "Private subscription" };
        return new BackupData
        {
            Settings = new AppSettings { Theme = ThemePreference.Light, Routing = new RoutingSettings { ProxyDomains = ["proxy.example"], DirectCidrs = ["10.8.0.0/16"] } },
            Profiles = [new(new Profile { Name = "Private server", Protocol = ProtocolKind.Vless, Host = "server.example", Port = 443, SubscriptionId = sub.Id },
                new ProfileSecrets { Values = { [Keys.Uuid] = "very-private-uuid", [Keys.Password] = "very-private-password", [Keys.RawConfig] = "PrivateKey = very-private-key" } })],
            Subscriptions = [new(sub, "https://subscription.example/token-private")],
            ToolSecrets = new ProfileSecrets { Values = { ["secret"] = "very-private-tool-secret" } },
        };
    }

    [Fact]
    public void Whole_archive_is_encrypted_and_roundtrips_with_fresh_salt_and_nonce()
    {
        var data = Data();
        var first = BackupArchive.Encrypt(data, Password);
        var second = BackupArchive.Encrypt(data, Password);
        Assert.False(first.SequenceEqual(second));
        var visible = Encoding.UTF8.GetString(first);
        foreach (var value in new[] { "very-private-uuid", "very-private-password", "very-private-key", "very-private-tool-secret", "token-private", "Private server", "server.example", "proxy.example", Password })
        { Assert.DoesNotContain(value, visible, StringComparison.Ordinal); }
        var restored = BackupArchive.Decrypt(first, Password);
        Assert.Equal(data.Profiles[0].Profile.Id, restored.Profiles[0].Profile.Id);
        Assert.Equal(data.Profiles[0].Secrets.Values, restored.Profiles[0].Secrets.Values);
        Assert.Equal(data.Subscriptions[0].Url, restored.Subscriptions[0].Url);
        Assert.Equal(data.ToolSecrets["secret"], restored.ToolSecrets["secret"]);
        Assert.Equal(data.Settings.Routing.ProxyDomains, restored.Settings.Routing.ProxyDomains);
    }

    [Fact]
    public void Wrong_password_tampering_and_truncation_are_rejected()
    {
        var file = BackupArchive.Encrypt(Data(), Password);
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.Decrypt(file, "wrong password"));
        foreach (var offset in new[] { 8, 24, 36, 52, file.Length - 1 })
        {
            var corrupt = file.ToArray();
            corrupt[offset] ^= 1;
            Assert.ThrowsAny<CryptographicException>(() => BackupArchive.Decrypt(corrupt, Password));
        }
        var badVersion = file.ToArray();
        badVersion[7] = 2;
        Assert.Throws<InvalidDataException>(() => BackupArchive.Decrypt(badVersion, Password));
        Assert.Throws<InvalidDataException>(() => BackupArchive.Decrypt(file[..30], Password));
        Assert.Throws<ArgumentException>(() => BackupArchive.Encrypt(Data(), ""));
    }

    [Theory]
    [InlineData(BackupImportMode.Merge)]
    [InlineData(BackupImportMode.Replace)]
    public async Task Restore_matches_preview_and_preserves_relationships_and_protected_secrets(BackupImportMode mode)
    {
        var folder = TestDirs.New("backup");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var incoming = Data();
        var existing = new Profile { Id = incoming.Profiles[0].Profile.Id, Name = "Old name", Protocol = ProtocolKind.Vless };
        var unrelated = new Profile { Name = "Keep only when merging", Protocol = ProtocolKind.Vless };
        await store.AddAsync([new(existing, new()), new(unrelated, new ProfileSecrets { Values = { [Keys.Password] = "old-secret" } })]);
        var oldSub = await store.AddSubscriptionAsync("Old subscription", "https://old.example/private");
        var preview = BackupArchive.Preview(incoming, store, mode);
        Assert.Equal(0, preview.AddedProfiles);
        Assert.Equal(1, preview.ReplacedProfiles);
        Assert.Equal(mode == BackupImportMode.Replace ? 1 : 0, preview.RemovedProfiles);
        Assert.Equal(1, preview.AddedSubscriptions);
        Assert.Equal(mode == BackupImportMode.Replace ? 1 : 0, preview.RemovedSubscriptions);
        await store.RestoreBackupAsync(BackupArchive.Decrypt(BackupArchive.Encrypt(incoming, Password), Password), mode);
        var reopened = new ProfileStore(Path.Combine(folder, "profiles.json"), store.Secrets);
        await reopened.LoadAsync();
        Assert.Equal(mode == BackupImportMode.Merge ? 2 : 1, reopened.Profiles.Count);
        var profile = Assert.Single(reopened.Profiles, p => p.Id == existing.Id);
        Assert.Equal("Private server", profile.Name);
        Assert.Equal(incoming.Subscriptions[0].Subscription.Id, profile.SubscriptionId);
        Assert.Equal(incoming.Subscriptions[0].Url, await reopened.GetSubscriptionUrlAsync(profile.SubscriptionId!.Value));
        Assert.Equal("very-private-uuid", (await reopened.Secrets.LoadAsync(profile.Id.ToString()))[Keys.Uuid]);
        Assert.Equal("very-private-tool-secret", (await reopened.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Equal(mode == BackupImportMode.Merge ? "old-secret" : null, (await reopened.Secrets.LoadAsync(unrelated.Id.ToString()))[Keys.Password]);
        Assert.Equal(mode == BackupImportMode.Merge ? "https://old.example/private" : null, await reopened.GetSubscriptionUrlAsync(oldSub.Id));
        var json = await File.ReadAllTextAsync(Path.Combine(folder, "profiles.json"));
        Assert.DoesNotContain("very-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token-private", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshot_includes_subscription_and_tool_credentials_but_excludes_device_paths()
    {
        var store = new ProfileStore(Path.Combine(TestDirs.New("backup"), "profiles.json"), new InMemorySecretStore());
        await store.RestoreBackupAsync(Data(), BackupImportMode.Replace);
        var settings = new AppSettings { EnginePaths = { ["Xray"] = "device-specific" }, Zapret = new ZapretSettings { InstallPath = "device-specific" } };
        var snapshot = await store.CaptureBackupAsync(settings);
        Assert.Empty(snapshot.Settings.EnginePaths);
        Assert.Null(snapshot.Settings.Zapret.InstallPath);
        Assert.NotEmpty(settings.EnginePaths);
        Assert.Equal("https://subscription.example/token-private", snapshot.Subscriptions[0].Url);
        Assert.Equal("very-private-tool-secret", snapshot.ToolSecrets["secret"]);
    }

    [Theory]
    [InlineData(BackupImportMode.Merge)]
    [InlineData(BackupImportMode.Replace)]
    public void Settings_import_preserves_device_integration_and_merges_only_routing_lists(BackupImportMode mode)
    {
        var data = Data();
        data.Settings.StartWithSystem = data.Settings.ConnectOnStartup = data.Settings.MockMode = true;
        data.Settings.EnginePaths["Xray"] = "foreign-path";
        data.Settings.Zapret.InstallPath = "foreign-folder";
        var current = new AppSettings { EnginePaths = { ["Xray"] = "local-path" }, Zapret = new ZapretSettings { InstallPath = "local-folder" }, Routing = new RoutingSettings { ProxyDomains = ["local.example", "PROXY.EXAMPLE"] } };
        var settings = BackupArchive.ImportSettings(data, current, mode);
        Assert.False(settings.StartWithSystem);
        Assert.False(settings.ConnectOnStartup);
        Assert.False(settings.MockMode);
        Assert.Equal("local-path", settings.EnginePaths["Xray"]);
        Assert.Equal("local-folder", settings.Zapret.InstallPath);
        Assert.Equal(ThemePreference.Light, settings.Theme);
        Assert.Equal(mode == BackupImportMode.Merge ? 2 : 1, settings.Routing.ProxyDomains.Count);
        Assert.Single(data.Settings.Routing.ProxyDomains);
    }

    [Fact]
    public async Task Invalid_data_and_storage_failure_leave_original_profiles_and_secrets_intact()
    {
        var secrets = new FailingSecretStore();
        var store = new ProfileStore(Path.Combine(TestDirs.New("backup"), "profiles.json"), secrets);
        var data = Data();
        await store.RestoreBackupAsync(data, BackupImportMode.Replace);
        var oldId = data.Profiles[0].Profile.Id;
        var next = Data();
        secrets.SavesUntilFailure = 1;
        using var session = BackupArchive.OpenImport(BackupArchive.Encrypt(next, Password), Password);
        await Assert.ThrowsAsync<IOException>(() => store.RestoreBackupAsync(session.Materialize(), BackupImportMode.Replace));
        Assert.Equal(oldId, Assert.Single(store.Profiles).Id);
        Assert.Equal("very-private-uuid", (await secrets.LoadAsync(oldId.ToString()))[Keys.Uuid]);
        Assert.Empty((await secrets.LoadAsync(next.Profiles[0].Profile.Id.ToString())).Values);
        next.Profiles.Add(next.Profiles[0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RestoreBackupAsync(next, BackupImportMode.Replace));
        Assert.Equal(oldId, Assert.Single(store.Profiles).Id);
        await store.LoadAsync();
        Assert.Equal(oldId, Assert.Single(store.Profiles).Id);
    }

    [Fact]
    public async Task Failed_document_write_restores_removed_credentials_and_original_document()
    {
        var folder = TestDirs.New("backup");
        var path = Path.Combine(folder, "profiles.json");
        var store = new ProfileStore(path, new InMemorySecretStore());
        var first = Data();
        await store.RestoreBackupAsync(first, BackupImportMode.Replace);
        var next = Data();
        // Force failure after incoming credentials were saved, before obsolete credentials are deleted.
        Directory.CreateDirectory(path + ".tmp");
        using var session = BackupArchive.OpenImport(BackupArchive.Encrypt(next, Password), Password);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.RestoreBackupAsync(session.Materialize(), BackupImportMode.Replace));
        await store.LoadAsync();
        Assert.Equal(first.Profiles[0].Profile.Id, Assert.Single(store.Profiles).Id);
        Assert.Equal("very-private-uuid", (await store.Secrets.LoadAsync(first.Profiles[0].Profile.Id.ToString()))[Keys.Uuid]);
        Assert.Equal(first.Subscriptions[0].Url, await store.GetSubscriptionUrlAsync(first.Subscriptions[0].Subscription.Id));
        Assert.Empty((await store.Secrets.LoadAsync(next.Profiles[0].Profile.Id.ToString())).Values);
        Assert.Null(await store.GetSubscriptionUrlAsync(next.Subscriptions[0].Subscription.Id));
    }

    [Fact]
    public async Task Merge_updates_only_tool_secrets_present_in_archive()
    {
        var store = new ProfileStore(Path.Combine(TestDirs.New("backup-merge-tools"), "profiles.json"), new InMemorySecretStore());
        await store.Secrets.SaveAsync(ConnectionManager.TgWsProxySecretId, new ProfileSecrets { Values = { ["secret"] = "old-tg" } });
        await store.Secrets.SaveAsync(ConnectionManager.LocalProxySecretId, new ProfileSecrets { Values = { ["password"] = "old-lan" } });

        // Archive carries a fresh Telegram secret but no LAN password.
        var data = new BackupData { ToolSecrets = new ProfileSecrets { Values = { ["secret"] = "new-tg" } } };
        await store.RestoreBackupAsync(data, BackupImportMode.Merge);
        Assert.Equal("new-tg", (await store.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Equal("old-lan", (await store.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId))["password"]);

        // Reverse direction: archive carries only the LAN password.
        var reverse = new BackupData { ToolSecrets = new ProfileSecrets { Values = { ["localproxy-password"] = "new-lan" } } };
        await store.RestoreBackupAsync(reverse, BackupImportMode.Merge);
        Assert.Equal("new-tg", (await store.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Equal("new-lan", (await store.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId))["password"]);
    }

    [Fact]
    public async Task Merge_with_empty_tool_bag_keeps_existing_tool_secrets()
    {
        var store = new ProfileStore(Path.Combine(TestDirs.New("backup-merge-tools-empty"), "profiles.json"), new InMemorySecretStore());
        await store.Secrets.SaveAsync(ConnectionManager.TgWsProxySecretId, new ProfileSecrets { Values = { ["secret"] = "old-tg" } });
        await store.Secrets.SaveAsync(ConnectionManager.LocalProxySecretId, new ProfileSecrets { Values = { ["password"] = "old-lan" } });

        await store.RestoreBackupAsync(new BackupData(), BackupImportMode.Merge);
        Assert.Equal("old-tg", (await store.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Equal("old-lan", (await store.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId))["password"]);
    }

    [Fact]
    public async Task Replace_with_empty_tool_bag_clears_tool_secrets()
    {
        var store = new ProfileStore(Path.Combine(TestDirs.New("backup-replace-tools-empty"), "profiles.json"), new InMemorySecretStore());
        await store.Secrets.SaveAsync(ConnectionManager.TgWsProxySecretId, new ProfileSecrets { Values = { ["secret"] = "old-tg" } });
        await store.Secrets.SaveAsync(ConnectionManager.LocalProxySecretId, new ProfileSecrets { Values = { ["password"] = "old-lan" } });

        await store.RestoreBackupAsync(new BackupData(), BackupImportMode.Replace);
        Assert.Null((await store.Secrets.LoadAsync(ConnectionManager.TgWsProxySecretId))["secret"]);
        Assert.Null((await store.Secrets.LoadAsync(ConnectionManager.LocalProxySecretId))["password"]);
    }

    [Fact]
    public async Task Rollback_continues_after_one_credential_cannot_be_restored()
    {
        var path = Path.Combine(TestDirs.New("backup-partial-rollback"), "profiles.json");
        var secrets = new FailingSecretStore();
        var store = new ProfileStore(path, secrets);
        var first = new Profile { Name = "First", Protocol = ProtocolKind.Trojan, Host = "first.test", Port = 443 };
        var second = new Profile { Name = "Second", Protocol = ProtocolKind.Trojan, Host = "second.test", Port = 443 };
        ProfileSecrets Password(string value) => new() { Values = { [Keys.Password] = value } };
        await store.AddAsync([new(first, Password("old-first")), new(second, Password("old-second"))]);
        var archive = new BackupData { Profiles = [new(first, Password("new-first")), new(second, Password("new-second"))] };
        Directory.CreateDirectory(path + ".tmp");
        secrets.RejectedPassword = "old-first";

        var error = await Assert.ThrowsAsync<AggregateException>(() => store.RestoreBackupAsync(archive, BackupImportMode.Merge));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Equal("old-second", (await secrets.LoadAsync(second.Id.ToString()))[Keys.Password]);
    }

    private sealed class FailingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public int SavesUntilFailure { get; set; } = -1;
        public string? RejectedPassword { get; set; }
        public string ProtectionDescription => "Test";
        public Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
        {
            if (RejectedPassword is not null && secrets[Keys.Password] == RejectedPassword)
            { throw new IOException("Simulated rollback failure."); }
            if (SavesUntilFailure == 0) { SavesUntilFailure = -1; throw new IOException("Simulated storage failure."); }
            if (SavesUntilFailure > 0) { SavesUntilFailure--; }
            return _inner.SaveAsync(id, secrets, ct);
        }
        public Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) => _inner.LoadAsync(id, ct);
        public Task DeleteAsync(string id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
    }
}
