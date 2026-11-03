using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class ProfileRemovalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failed_document_write_keeps_memory_disk_chains_and_credentials(bool subscription, bool cancel)
    {
        var file = Path.Combine(TestDirs.New("remove-write-failure"), "profiles.json");
        var secrets = new RemovalSecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("first");
        var second = Import("second");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [first, second]);
        var chain = await store.AddChainAsync("Relay", [first.Profile.Id, second.Profile.Id]);
        var original = await File.ReadAllTextAsync(file);
        using var cts = new CancellationTokenSource();
        if (cancel) { await cts.CancelAsync(); }
        else { Directory.CreateDirectory(file + ".tmp"); }

        var error = await Record.ExceptionAsync(() => Remove(store, subscription, sub.Id, first.Profile.Id, cts.Token));
        if (cancel) { Assert.IsAssignableFrom<OperationCanceledException>(error); }
        else { Assert.True(error is IOException or UnauthorizedAccessException); }
        Assert.Equal(0, secrets.DeleteCalls);
        Assert.Equal(original, await File.ReadAllTextAsync(file));
        await AssertOriginal(store);
        var reloaded = new ProfileStore(file, secrets);
        await reloaded.LoadAsync();
        await AssertOriginal(reloaded);

        if (!cancel) { Directory.Delete(file + ".tmp"); }
        await Remove(store, subscription, sub.Id, first.Profile.Id);
        await Remove(store, subscription, sub.Id, first.Profile.Id);
        await reloaded.LoadAsync();
        Assert.DoesNotContain(reloaded.Profiles, p => p.Id == first.Profile.Id);
        Assert.Empty(reloaded.Chains);
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        if (subscription)
        {
            Assert.Empty(reloaded.Profiles);
            Assert.Empty(reloaded.Subscriptions);
            Assert.Null(await reloaded.GetSubscriptionUrlAsync(sub.Id));
            Assert.Empty((await secrets.LoadAsync(second.Profile.Id.ToString())).Values);
        }
        else
        {
            Assert.Equal(second.Profile.Id, Assert.Single(reloaded.Profiles).Id);
            Assert.Equal("second-password", (await secrets.LoadAsync(second.Profile.Id.ToString()))[Keys.Password]);
        }

        async Task AssertOriginal(ProfileStore actual)
        {
            Assert.Equal(new[] { first.Profile.Id, second.Profile.Id }, actual.Profiles.Select(p => p.Id));
            Assert.Equal(sub.Id, Assert.Single(actual.Subscriptions).Id);
            Assert.Equal(chain.ProfileIds, Assert.Single(actual.Chains).ProfileIds);
            Assert.Equal("first-password", (await secrets.LoadAsync(first.Profile.Id.ToString()))[Keys.Password]);
            Assert.Equal("second-password", (await secrets.LoadAsync(second.Profile.Id.ToString()))[Keys.Password]);
            Assert.Equal("https://sub.example/token", await actual.GetSubscriptionUrlAsync(sub.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_secret_cleanup_can_be_retried_after_restart_and_other_edits(bool subscription)
    {
        var file = Path.Combine(TestDirs.New("remove-cleanup-failure"), "profiles.json");
        var secrets = new RemovalSecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("first");
        var second = Import("second");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [first, second]);
        // Fail partway through subscription cleanup, after the first profile secret was deleted.
        secrets.FailDeleteId = (subscription ? second : first).Profile.Id.ToString();
        await Assert.ThrowsAsync<IOException>(() => Remove(store, subscription, sub.Id, first.Profile.Id));
        Assert.DoesNotContain(store.Profiles, p => p.Id == first.Profile.Id);
        var reloaded = new ProfileStore(file, secrets);
        await reloaded.LoadAsync();
        Assert.DoesNotContain(reloaded.Profiles, p => p.Id == first.Profile.Id);
        await reloaded.AddAsync([Import("unrelated")]);

        secrets.FailDeleteId = null;
        store = new ProfileStore(file, secrets);
        await store.LoadAsync();
        await Remove(store, subscription, sub.Id, first.Profile.Id);
        await Remove(store, subscription, sub.Id, first.Profile.Id);
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        if (subscription)
        {
            Assert.Empty((await secrets.LoadAsync(second.Profile.Id.ToString())).Values);
            Assert.Null(await store.GetSubscriptionUrlAsync(sub.Id));
        }
        Assert.DoesNotContain("subscription:", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_commit_does_not_interrupt_secret_cleanup(bool subscription)
    {
        var file = Path.Combine(TestDirs.New("remove-cleanup-cancel"), "profiles.json");
        using var cts = new CancellationTokenSource();
        var secrets = new RemovalSecretStore { OnDelete = cts.Cancel };
        var store = new ProfileStore(file, secrets);
        var first = Import("first");
        var second = Import("second");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://sub.example/token", [first, second]);

        await Remove(store, subscription, sub.Id, first.Profile.Id, cts.Token);

        Assert.True(cts.IsCancellationRequested);
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        if (subscription)
        {
            Assert.Empty((await secrets.LoadAsync(second.Profile.Id.ToString())).Values);
            Assert.Null(await store.GetSubscriptionUrlAsync(sub.Id));
        }
        await store.LoadAsync();
        Assert.DoesNotContain(store.Profiles, p => p.Id == first.Profile.Id);
    }

    private static ImportedProfile Import(string name) => Assert.Single(new ProfileImporter()
        .ImportText($"trojan://{name}-password@{name}.example:443#{name}").Profiles);

    [Fact]
    public async Task Failed_cleanup_journal_write_keeps_committed_removal_and_can_be_retried()
    {
        var file = Path.Combine(TestDirs.New("remove-journal-failure"), "profiles.json");
        var secrets = new RemovalSecretStore { OnDelete = () => Directory.CreateDirectory(file + ".tmp") };
        var store = new ProfileStore(file, secrets);
        var first = Import("first");
        await store.AddAsync([first]);

        var error = await Record.ExceptionAsync(() => store.RemoveAsync(first.Profile.Id));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(store.Profiles);
        Assert.Empty((await secrets.LoadAsync(first.Profile.Id.ToString())).Values);
        await store.LoadAsync();
        Assert.Empty(store.Profiles);
        secrets.OnDelete = null;
        Directory.Delete(file + ".tmp");
        await store.RemoveAsync(first.Profile.Id);
        Assert.Empty(store.Profiles);
    }

    [Fact]
    public async Task Retried_cleanup_does_not_delete_credentials_for_a_restored_profile()
    {
        var file = Path.Combine(TestDirs.New("remove-restored-profile"), "profiles.json");
        var secrets = new RemovalSecretStore();
        var store = new ProfileStore(file, secrets);
        var first = Import("first");
        await store.AddAsync([first]);
        secrets.FailDeleteId = first.Profile.Id.ToString();
        await Assert.ThrowsAsync<IOException>(() => store.RemoveAsync(first.Profile.Id));
        await store.AddAsync([first]);
        secrets.FailDeleteId = null;

        await store.RemoveAsync(Guid.NewGuid());

        Assert.Equal(first.Profile.Id, Assert.Single(store.Profiles).Id);
        Assert.Equal("first-password", (await secrets.LoadAsync(first.Profile.Id.ToString()))[Keys.Password]);
    }

    private static Task Remove(ProfileStore store, bool subscription, Guid subId, Guid profileId, CancellationToken ct = default) =>
        subscription ? store.RemoveSubscriptionAsync(subId, ct) : store.RemoveAsync(profileId, ct);

    private sealed class RemovalSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public string ProtectionDescription => _inner.ProtectionDescription;
        public string? FailDeleteId { get; set; }
        public Action? OnDelete { get; set; }
        public int DeleteCalls { get; private set; }
        public Task SaveAsync(string id, ProfileSecrets secrets, CancellationToken ct = default) => _inner.SaveAsync(id, secrets, ct);
        public Task<ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) => _inner.LoadAsync(id, ct);
        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            DeleteCalls++;
            OnDelete?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (id == FailDeleteId) { throw new IOException("Simulated secret cleanup failure."); }
            return _inner.DeleteAsync(id, ct);
        }
    }
}
