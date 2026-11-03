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
    public async Task Remove_subscription_detaches_all_deleted_profiles_from_persisted_chains()
    {
        var file = Path.Combine(TestDirs.New("sub-remove-chains"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("trojan://first-password@first.example:443#First");
        var second = Import("trojan://second-password@second.example:443#Second");
        var sub = await store.AddSubscriptionWithProfilesAsync("Removed", "https://sub.example/token", [first, second]);
        var entry = Import("trojan://entry-password@entry.example:443#Entry");
        var exit = Import("trojan://exit-password@exit.example:443#Exit");
        var other = Import("trojan://other-password@other.example:443#Other");
        var otherSub = await store.AddSubscriptionWithProfilesAsync("Kept", "https://other.example/token", [other]);
        await store.AddAsync([entry, exit]);

        await store.AddChainAsync("Too short", [entry.Profile.Id, first.Profile.Id]);
        await store.AddChainAsync("All removed", [first.Profile.Id, second.Profile.Id]);
        var shortened = await store.AddChainAsync("Shortened", [first.Profile.Id, entry.Profile.Id, second.Profile.Id, exit.Profile.Id], EngineKind.Xray);
        var untouched = await store.AddChainAsync("Untouched", [exit.Profile.Id, other.Profile.Id]);

        await store.RemoveSubscriptionAsync(sub.Id);

        AssertRemainingState(store);
        var reloaded = new ProfileStore(file, secrets);
        await reloaded.LoadAsync();
        AssertRemainingState(reloaded);
        Assert.Null(await reloaded.GetSubscriptionUrlAsync(sub.Id));
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        Assert.Empty((await secrets.LoadAsync(second.Profile.Id.ToString())).Values);
        Assert.Equal("https://other.example/token", await reloaded.GetSubscriptionUrlAsync(otherSub.Id));
        Assert.Equal("entry-password", (await secrets.LoadAsync(entry.Profile.Id.ToString()))[Keys.Password]);
        Assert.Equal("exit-password", (await secrets.LoadAsync(exit.Profile.Id.ToString()))[Keys.Password]);
        Assert.Equal("other-password", (await secrets.LoadAsync(other.Profile.Id.ToString()))[Keys.Password]);

        void AssertRemainingState(ProfileStore actual)
        {
            Assert.Equal(otherSub.Id, Assert.Single(actual.Subscriptions).Id);
            Assert.Equal(new[] { other.Profile.Id, entry.Profile.Id, exit.Profile.Id }, actual.Profiles.Select(p => p.Id));
            Assert.Equal(2, actual.Chains.Count);
            var kept = Assert.Single(actual.Chains, c => c.Id == shortened.Id);
            Assert.Equal("Shortened", kept.Name);
            Assert.Equal(EngineKind.Xray, kept.PreferredEngine);
            Assert.Equal(new[] { entry.Profile.Id, exit.Profile.Id }, kept.ProfileIds);
            Assert.Equal(untouched.ProfileIds, Assert.Single(actual.Chains, c => c.Id == untouched.Id).ProfileIds);
            Assert.All(actual.Chains.SelectMany(c => c.ProfileIds), id => Assert.Contains(actual.Profiles, p => p.Id == id));
        }
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

    [Fact]
    public async Task Selected_only_refresh_keeps_partial_selection()
    {
        var file = Path.Combine(TestDirs.New("sub-selected-only"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var kept = Import("trojan://password-a@a.example:443#Kept");
        var sub = await store.AddSubscriptionWithProfilesAsync("Partial", "https://sub.example/token", [kept], mode: SubscriptionMode.SelectedOnly);
        Assert.Equal(SubscriptionMode.SelectedOnly, sub.Mode);

        var keptFresh = Import("trojan://password-a@a.example:443#Kept renamed");
        var deselectedFresh = Import("trojan://password-b@b.example:443#Deselected");
        await store.ReplaceSubscriptionProfilesAsync(sub.Id, [keptFresh, deselectedFresh]);

        Assert.Equal(kept.Profile.Id, Assert.Single(store.Profiles).Id);
        Assert.Equal(SubscriptionMode.SelectedOnly, Assert.Single(store.Subscriptions).Mode);
        Assert.Equal("password-a", (await secrets.LoadAsync(kept.Profile.Id.ToString()))[Keys.Password]);
        Assert.Empty((await secrets.LoadAsync(deselectedFresh.Profile.Id.ToString())).Values);
    }

    [Fact]
    public async Task Follow_all_refresh_adds_new_nodes()
    {
        var file = Path.Combine(TestDirs.New("sub-follow-all"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("trojan://password-a@a.example:443#First");
        var sub = await store.AddSubscriptionWithProfilesAsync("Full", "https://sub.example/token", [first], mode: SubscriptionMode.FollowAll);

        var addedFresh = Import("trojan://password-b@b.example:443#New");
        await store.ReplaceSubscriptionProfilesAsync(sub.Id,
        [
            Import("trojan://password-a@a.example:443#First"),
            addedFresh,
        ]);

        Assert.Equal(2, store.Profiles.Count);
        Assert.Contains(store.Profiles, p => p.Id == first.Profile.Id);
        Assert.Contains(store.Profiles, p => p.Id == addedFresh.Profile.Id);
        Assert.Equal("password-b", (await secrets.LoadAsync(addedFresh.Profile.Id.ToString()))[Keys.Password]);
        Assert.Equal(SubscriptionMode.FollowAll, Assert.Single(store.Subscriptions).Mode);
    }

    [Fact]
    public async Task Subscription_mode_survives_restart_and_toggle()
    {
        var file = Path.Combine(TestDirs.New("sub-mode-restart"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());
        var first = Import("trojan://password-a@a.example:443#First");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [first], mode: SubscriptionMode.SelectedOnly);

        await store.LoadAsync();
        Assert.Equal(SubscriptionMode.SelectedOnly, Assert.Single(store.Subscriptions).Mode);

        await store.SetSubscriptionModeAsync(sub.Id, SubscriptionMode.FollowAll);
        Assert.Equal(SubscriptionMode.FollowAll, Assert.Single(store.Subscriptions).Mode);

        var reloaded = new ProfileStore(file, new InMemorySecretStore());
        await reloaded.LoadAsync();
        Assert.Equal(SubscriptionMode.FollowAll, Assert.Single(reloaded.Subscriptions).Mode);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetSubscriptionModeAsync(Guid.NewGuid(), SubscriptionMode.FollowAll));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetSubscriptionModeAsync(sub.Id, (SubscriptionMode)99));
    }

    [Fact]
    public async Task Selected_only_refresh_drops_vanished_node()
    {
        var file = Path.Combine(TestDirs.New("sub-selected-vanished"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("trojan://password-a@a.example:443#First");
        var gone = Import("trojan://password-gone@gone.example:443#Gone");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [first, gone], mode: SubscriptionMode.SelectedOnly);

        await store.ReplaceSubscriptionProfilesAsync(sub.Id, [Import("trojan://password-a@a.example:443#First")]);

        Assert.Equal(first.Profile.Id, Assert.Single(store.Profiles).Id);
        Assert.Empty((await secrets.LoadAsync(gone.Profile.Id.ToString())).Values);
    }

    [Fact]
    public async Task Legacy_subscription_without_mode_follows_all()
    {
        var file = Path.Combine(TestDirs.New("sub-legacy-mode"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());
        var sub = await store.AddSubscriptionAsync("Legacy", "https://sub.example/token");
        var old = Import("trojan://old-password@old.example:443#Old");
        await store.AddAsync([old], sub.Id);
        await store.LoadAsync();

        Assert.Equal(SubscriptionMode.FollowAll, Assert.Single(store.Subscriptions).Mode);

        await store.ReplaceSubscriptionProfilesAsync(sub.Id,
        [
            Import("trojan://old-password@old.example:443#Old"),
            Import("trojan://new-password@new.example:443#New"),
        ]);

        Assert.Equal(2, store.Profiles.Count);
    }

    [Fact]
    public async Task Missing_mode_field_in_json_defaults_to_follow_all()
    {
        var folder = TestDirs.New("sub-mode-missing-field");
        var file = Path.Combine(folder, "profiles.json");
        var subId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        await File.WriteAllTextAsync(file,
            $$"""{"Version":1,"Profiles":[{"Id":"{{profileId}}","Name":"Old","Protocol":2,"Host":"old.example","Port":443,"Options":{},"SubscriptionId":"{{subId}}"}],"Subscriptions":[{"Id":"{{subId}}","Name":"Legacy"}],"Chains":[],"PendingSecretDeletes":[]}""");
        var store = new ProfileStore(file, new InMemorySecretStore());
        await store.LoadAsync();

        Assert.Equal(SubscriptionMode.FollowAll, Assert.Single(store.Subscriptions).Mode);
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
