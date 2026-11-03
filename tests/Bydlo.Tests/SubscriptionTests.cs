using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class SubscriptionTests
{
    private static ImportedProfile Import(string text) =>
        Assert.Single(new ProfileImporter().ImportText(text).Profiles);

    [Fact]
    public async Task Add_subscription_persists_userinfo_metadata()
    {
        var folder = TestDirs.New("sub-meta");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://pw@server.example:443#Test");
        var metadata = new SubscriptionMetadata(10, 30, 100, DateTimeOffset.FromUnixTimeSeconds(2000000000));
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", imported.Profiles, metadata);
        Assert.Equal(10, sub.Upload);
        Assert.Equal(30, sub.Download);
        Assert.Equal(100, sub.Total);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), sub.Expires);
        Assert.NotNull(sub.LastUpdated);

        await store.LoadAsync();
        var reloaded = Assert.Single(store.Subscriptions);
        Assert.Equal(100, reloaded.Total);
    }

    [Fact]
    public async Task Replace_subscription_updates_metadata()
    {
        var folder = TestDirs.New("sub-replace-meta");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var first = new ProfileImporter().ImportText("trojan://pw@server.example:443#Test");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", first.Profiles);
        var fresh = new ProfileImporter().ImportText("trojan://pw2@server2.example:443#Test2");
        var metadata = new SubscriptionMetadata(5, 15, 50, null);
        await store.ReplaceSubscriptionProfilesAsync(sub.Id, fresh.Profiles, metadata);
        var updated = Assert.Single(store.Subscriptions);
        Assert.Equal(50, updated.Total);
        Assert.Equal(5, updated.Upload);
        Assert.NotNull(updated.LastUpdated);
        Assert.Single(store.Profiles);
    }

    [Fact]
    public async Task Rename_and_remove_subscription()
    {
        var folder = TestDirs.New("sub-rename-remove");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://pw@server.example:443#Test");
        var sub = await store.AddSubscriptionWithProfilesAsync("Old", "https://sub.example/token", imported.Profiles);
        await store.RenameSubscriptionAsync(sub.Id, "New");
        Assert.Equal("New", Assert.Single(store.Subscriptions).Name);
        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameSubscriptionAsync(sub.Id, "  "));

        await store.RemoveSubscriptionAsync(sub.Id);
        Assert.Empty(store.Subscriptions);
        Assert.Empty(store.Profiles);
        Assert.Null(await store.GetSubscriptionUrlAsync(sub.Id));

        var fresh = Import("trojan://new-password@server.example:443#Refreshed");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReplaceSubscriptionProfilesAsync(sub.Id, [fresh]));
        Assert.Empty(store.Profiles);
        Assert.Null((await store.Secrets.LoadAsync(fresh.Profile.Id.ToString()))["password"]);
    }

    [Fact]
    public async Task Refresh_preserves_settings_and_chain_order_across_reordering_and_removed_hop()
    {
        var file = Path.Combine(TestDirs.New("sub-identity"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("trojan://password-one@server.example:443#First");
        var second = Import("trojan://password-two@server.example:443#Second");
        var removed = Import("trojan://password-three@removed.example:443#Removed");
        var obsolete = Import("trojan://password-four@obsolete.example:443#Obsolete");
        var sub = await store.AddSubscriptionWithProfilesAsync("My subscription", "https://sub.example/token", [first, second, removed, obsolete]);
        var chain = await store.AddChainAsync("Relay", [first.Profile.Id, removed.Profile.Id, second.Profile.Id]);
        first.Profile.Name = "My server";
        first.Profile.IsFavorite = true;
        first.Profile.PreferredEngine = EngineKind.Xray;
        first.Profile.Options[Keys.Sni] = "local.example";
        await store.UpdateAsync(first.Profile);

        await store.ReplaceSubscriptionProfilesAsync(sub.Id,
        [
            Import("trojan://password-two@server.example:443?sni=second-upstream.example#Renamed second"),
            Import("trojan://password-one@server.example:443?sni=upstream.example#Renamed first"),
            Import("trojan://password-new@new.example:443#New"),
        ]);

        await store.LoadAsync();
        var kept = Assert.Single(store.Profiles, profile => profile.Id == first.Profile.Id);
        Assert.Equal("My server", kept.Name);
        Assert.True(kept.IsFavorite);
        Assert.Equal(EngineKind.Xray, kept.PreferredEngine);
        Assert.Equal("local.example", kept.Option(Keys.Sni));
        Assert.Equal("password-one", (await secrets.LoadAsync(kept.Id.ToString()))[Keys.Password]);
        Assert.Equal(chain.ProfileIds, Assert.Single(store.Chains).ProfileIds);
        Assert.Contains(store.Profiles, profile => profile.Id == removed.Profile.Id);
        Assert.Equal("second-upstream.example", Assert.Single(store.Profiles, profile => profile.Id == second.Profile.Id).Option(Keys.Sni));
        Assert.DoesNotContain(store.Profiles, profile => profile.Id == obsolete.Profile.Id);
        Assert.Empty((await secrets.LoadAsync(obsolete.Profile.Id.ToString())).Values);
        Assert.DoesNotContain("password-one", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_refresh_preserves_persisted_profiles_and_secrets(bool failDocumentWrite)
    {
        var file = Path.Combine(TestDirs.New("sub-refresh-failure"), "profiles.json");
        var secrets = new RefreshSecretStore();
        var store = new ProfileStore(file, secrets);
        var old = Import("trojan://old-password@old.example:443#Old");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [old]);
        var initialDocument = await File.ReadAllTextAsync(file);
        var first = Import("trojan://first-password@first.example:443#First");
        var second = Import("trojan://second-password@second.example:443#Second");
        if (failDocumentWrite) { Directory.CreateDirectory(file + ".tmp"); }
        else { secrets.FailSaveId = second.Profile.Id.ToString(); }

        var error = await Record.ExceptionAsync(() => store.ReplaceSubscriptionProfilesAsync(sub.Id, [first, second]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
        Assert.Equal(old.Profile.Id, Assert.Single(store.Profiles).Id);
        Assert.Equal(sub.LastUpdated, Assert.Single(store.Subscriptions).LastUpdated);
        Assert.Equal("old-password", (await secrets.LoadAsync(old.Profile.Id.ToString()))[Keys.Password]);
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        Assert.Empty((await secrets.LoadAsync(second.Profile.Id.ToString())).Values);
    }

    [Fact]
    public async Task Legacy_subscription_keeps_current_options_without_a_provider_snapshot()
    {
        var file = Path.Combine(TestDirs.New("sub-legacy-options"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());
        var sub = await store.AddSubscriptionAsync("Legacy", "https://sub.example/token");
        var old = Import("trojan://fixture@server.example:443?sni=local.example#Original");
        await store.AddAsync([old], sub.Id);
        await store.LoadAsync();

        await store.ReplaceSubscriptionProfilesAsync(sub.Id,
            [Import("trojan://fixture@server.example:443?sni=upstream.example#Updated")]);

        var kept = Assert.Single(store.Profiles);
        Assert.Equal(old.Profile.Id, kept.Id);
        Assert.Equal("local.example", kept.Option(Keys.Sni));
    }

    private sealed class RefreshSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public string? FailSaveId { get; set; }
        public string ProtectionDescription => _inner.ProtectionDescription;
        public async Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default)
        {
            await _inner.SaveAsync(id, secrets, ct);
            if (id == FailSaveId) { throw new IOException("Simulated failure after writing."); }
        }

        public Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) => _inner.LoadAsync(id, ct);
        public Task DeleteAsync(string id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
    }
}
