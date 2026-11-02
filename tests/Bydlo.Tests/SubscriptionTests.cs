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
}
