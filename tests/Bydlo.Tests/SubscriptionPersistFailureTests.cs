using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Issue #399: failed subscription writes must leave memory == disk.</summary>
public sealed class SubscriptionPersistFailureTests
{
    [Fact]
    public async Task Rename_failure_keeps_memory_and_disk()
    {
        var file = Path.Combine(TestDirs.New("sub-399-rename"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var sub = await store.AddSubscriptionAsync("Before", "https://sub.example/token");
        var initialDocument = await File.ReadAllTextAsync(file);

        Directory.CreateDirectory(file + ".tmp");
        try
        {
            var error = await Record.ExceptionAsync(() => store.RenameSubscriptionAsync(sub.Id, "After"));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error?.GetType().FullName}");
            Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
            Assert.Equal("Before", Assert.Single(store.Subscriptions).Name);
            Assert.Equal("Before", sub.Name);
        }
        finally
        {
            Directory.Delete(file + ".tmp");
        }

        await store.RenameSubscriptionAsync(sub.Id, "After");
        Assert.Equal("After", Assert.Single(store.Subscriptions).Name);
    }

    [Fact]
    public async Task SetUserAgent_failure_keeps_memory_and_disk()
    {
        var file = Path.Combine(TestDirs.New("sub-399-ua"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(file, secrets);
        var sub = await store.AddSubscriptionAsync("Sub", "https://sub.example/token");
        await store.SetSubscriptionUserAgentAsync(sub.Id, "Before");
        var initialDocument = await File.ReadAllTextAsync(file);

        Directory.CreateDirectory(file + ".tmp");
        try
        {
            var error = await Record.ExceptionAsync(() => store.SetSubscriptionUserAgentAsync(sub.Id, "new-agent"));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error?.GetType().FullName}");
            Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
            Assert.Equal("Before", Assert.Single(store.Subscriptions).UserAgent);
        }
        finally
        {
            Directory.Delete(file + ".tmp");
        }

        await store.SetSubscriptionUserAgentAsync(sub.Id, "new-agent");
        Assert.Equal("new-agent", Assert.Single(store.Subscriptions).UserAgent);
    }

    [Fact]
    public async Task Add_failure_keeps_memory_disk_and_rolls_back_secret()
    {
        var file = Path.Combine(TestDirs.New("sub-399-add"), "profiles.json");
        var secrets = new TrackingSecretStore();
        var store = new ProfileStore(file, secrets);
        var existing = await store.AddSubscriptionAsync("Before", "https://before.example/token");
        var initialDocument = await File.ReadAllTextAsync(file);
        var savedBefore = new List<string>(secrets.SavedIds);

        Directory.CreateDirectory(file + ".tmp");
        try
        {
            var error = await Record.ExceptionAsync(() => store.AddSubscriptionAsync("New", "https://new.example/token"));
            Assert.True(error is IOException or UnauthorizedAccessException, $"Unexpected: {error?.GetType().FullName}");
            Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
            Assert.Equal(existing.Id, Assert.Single(store.Subscriptions).Id);
            Assert.Equal("Before", Assert.Single(store.Subscriptions).Name);
            Assert.Equal("https://before.example/token", await store.GetSubscriptionUrlAsync(existing.Id));
            foreach (var id in secrets.SavedIds.Except(savedBefore, StringComparer.Ordinal))
            {
                Assert.Empty((await secrets.LoadAsync(id)).Values);
            }
        }
        finally
        {
            Directory.Delete(file + ".tmp");
        }

        var added = await store.AddSubscriptionAsync("New", "https://new.example/token");
        Assert.Equal(2, store.Subscriptions.Count);
        Assert.Equal("https://new.example/token", await store.GetSubscriptionUrlAsync(added.Id));
    }

    [Fact]
    public async Task Add_secretSaveThrows_rolls_back_no_orphan()
    {
        var file = Path.Combine(TestDirs.New("sub-399-add-save-fail"), "profiles.json");
        var secrets = new SaveThenThrowSecretStore();
        var store = new ProfileStore(file, secrets);
        var existing = await store.AddSubscriptionAsync("Before", "https://before.example/token");
        var initialDocument = await File.ReadAllTextAsync(file);
        var savedBefore = new List<string>(secrets.SavedIds);

        secrets.FailNextSaveWithIo = true;
        var error = await Record.ExceptionAsync(() => store.AddSubscriptionAsync("New", "https://new.example/token"));
        Assert.IsType<IOException>(error);
        Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
        Assert.Equal(existing.Id, Assert.Single(store.Subscriptions).Id);
        Assert.Equal("Before", Assert.Single(store.Subscriptions).Name);
        Assert.Equal("https://before.example/token", await store.GetSubscriptionUrlAsync(existing.Id));
        foreach (var id in secrets.SavedIds.Except(savedBefore, StringComparer.Ordinal))
        {
            Assert.Empty((await secrets.LoadAsync(id)).Values);
        }
    }

    [Fact]
    public async Task Add_secretSaveCancelled_rolls_back_no_orphan()
    {
        var file = Path.Combine(TestDirs.New("sub-399-add-save-cancel"), "profiles.json");
        var secrets = new SaveThenThrowSecretStore();
        var store = new ProfileStore(file, secrets);
        var existing = await store.AddSubscriptionAsync("Before", "https://before.example/token");
        var initialDocument = await File.ReadAllTextAsync(file);
        var savedBefore = new List<string>(secrets.SavedIds);

        secrets.FailNextSaveWithCancel = true;
        var error = await Record.ExceptionAsync(() => store.AddSubscriptionAsync("New", "https://new.example/token"));
        Assert.IsType<OperationCanceledException>(error);
        Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
        Assert.Equal(existing.Id, Assert.Single(store.Subscriptions).Id);
        Assert.Equal("https://before.example/token", await store.GetSubscriptionUrlAsync(existing.Id));
        foreach (var id in secrets.SavedIds.Except(savedBefore, StringComparer.Ordinal))
        {
            Assert.Empty((await secrets.LoadAsync(id)).Values);
        }
    }

    [Fact]
    public async Task Cancelled_rename_keeps_memory_and_disk()
    {
        var file = Path.Combine(TestDirs.New("sub-399-cancel"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());
        var sub = await store.AddSubscriptionAsync("Before", "https://sub.example/token");
        var initialDocument = await File.ReadAllTextAsync(file);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RenameSubscriptionAsync(sub.Id, "After", cts.Token));
        Assert.Equal(initialDocument, await File.ReadAllTextAsync(file));
        Assert.Equal("Before", Assert.Single(store.Subscriptions).Name);
    }

    private sealed class SaveThenThrowSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public List<string> SavedIds { get; } = [];
        public bool FailNextSaveWithIo { get; set; }
        public bool FailNextSaveWithCancel { get; set; }
        public string ProtectionDescription => _inner.ProtectionDescription;

        public async Task SaveAsync(string id, Core.Models.ProfileSecrets secrets, CancellationToken ct = default)
        {
            // Persist first so the test proves rollback deletes a partial save (issue #399 review).
            await _inner.SaveAsync(id, secrets, CancellationToken.None);
            lock (SavedIds)
            {
                SavedIds.Add(id);
            }

            if (FailNextSaveWithIo)
            {
                FailNextSaveWithIo = false;
                throw new IOException("Simulated secret-save failure after write.");
            }

            if (FailNextSaveWithCancel)
            {
                FailNextSaveWithCancel = false;
                throw new OperationCanceledException(ct);
            }
        }

        public Task<Core.Models.ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) =>
            _inner.LoadAsync(id, ct);

        public Task DeleteAsync(string id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
    }

    private sealed class TrackingSecretStore : ISecretStore
    {
        private readonly InMemorySecretStore _inner = new();
        public List<string> SavedIds { get; } = [];
        public string ProtectionDescription => _inner.ProtectionDescription;
        public async Task SaveAsync(string id, Core.Models.ProfileSecrets secrets, CancellationToken ct = default)
        {
            await _inner.SaveAsync(id, secrets, ct);
            lock (SavedIds)
            {
                SavedIds.Add(id);
            }
        }

        public Task<Core.Models.ProfileSecrets> LoadAsync(string id, CancellationToken ct = default) =>
            _inner.LoadAsync(id, ct);

        public Task DeleteAsync(string id, CancellationToken ct = default) => _inner.DeleteAsync(id, ct);
    }
}
