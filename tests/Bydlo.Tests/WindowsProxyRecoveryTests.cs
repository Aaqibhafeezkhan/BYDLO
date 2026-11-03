using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class WindowsProxyRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_instance_restores_exact_values_after_crash(bool missing)
    {
        var directory = TestDirs.New("windows-proxy-crash");
        var registry = new FakeRegistry(directory);
        if (!missing)
        {
            registry.Values["ProxyEnable"] = new(null, 0);
            registry.Values["ProxyServer"] = new("%PROXY%:8888", null, true);
            registry.Values["ProxyOverride"] = new("", null);
        }
        var previous = new Dictionary<string, WindowsProxyValue>(registry.Values);
        using (var first = Service(registry, directory))
        {
            await first.SetAsync("127.0.0.1", 2080);
            Assert.Equal(new(null, 1), registry.Values["ProxyEnable"]);
        }
        using var next = Service(registry, directory);
        await next.ClearAsync();
        Assert.Equal(previous, registry.Values);
        Assert.False(File.Exists(registry.Journal));
        var writes = registry.Writes;
        await next.ClearAsync();
        Assert.Equal(writes, registry.Writes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Partial_apply_survives_failed_rollback_and_restart(int successfulWrites)
    {
        var directory = TestDirs.New("windows-proxy-partial");
        var registry = new FakeRegistry(directory) { FailAfter = successfulWrites, FailNotify = successfulWrites == 3 };
        registry.Values["ProxyEnable"] = new(null, 0);
        var previous = new Dictionary<string, WindowsProxyValue>(registry.Values);
        using (var first = Service(registry, directory))
        {
            await Assert.ThrowsAsync<IOException>(() => first.SetAsync("127.0.0.1", 2080));
        }
        // If the first write failed, rollback has nothing to restore and removes the journal.
        Assert.Equal(successfulWrites > 0, File.Exists(registry.Journal));
        registry.FailAfter = int.MaxValue;
        registry.FailNotify = false;
        using var next = Service(registry, directory);
        await next.ClearAsync();
        Assert.Equal(previous, registry.Values);
        Assert.False(File.Exists(registry.Journal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Interrupted_restore_is_retryable(int successfulWrites)
    {
        var directory = TestDirs.New("windows-proxy-restore");
        var registry = new FakeRegistry(directory);
        using (var first = Service(registry, directory)) { await first.SetAsync("127.0.0.1", 2080); }
        registry.FailAfter = registry.Writes + successfulWrites;
        using (var next = Service(registry, directory)) { await Assert.ThrowsAsync<IOException>(() => next.ClearAsync()); }
        Assert.True(File.Exists(registry.Journal));
        registry.FailAfter = int.MaxValue;
        using var retry = Service(registry, directory);
        await retry.ClearAsync();
        Assert.Empty(registry.Values);
    }

    [Fact]
    public async Task External_endpoint_and_enable_flag_are_preserved()
    {
        var directory = TestDirs.New("windows-proxy-external");
        var registry = new FakeRegistry(directory);
        registry.Values["ProxyEnable"] = new(null, 0);
        using (var first = Service(registry, directory)) { await first.SetAsync("127.0.0.1", 2080); }
        registry.Values["ProxyServer"] = new("another-app:9000", null);
        registry.Values["ProxyOverride"] = new("other-bypass", null);
        var external = new Dictionary<string, WindowsProxyValue>(registry.Values);
        using var next = Service(registry, directory);
        await next.ClearAsync();
        Assert.Equal(external, registry.Values);
    }

    [Fact]
    public async Task External_disable_is_preserved_while_owned_values_are_restored()
    {
        var directory = TestDirs.New("windows-proxy-disable");
        var registry = new FakeRegistry(directory);
        using var service = Service(registry, directory);
        await service.SetAsync("127.0.0.1", 2080);
        registry.Values["ProxyEnable"] = new(null, 0);
        await service.ClearAsync();
        Assert.Equal(new(null, 0), Assert.Single(registry.Values).Value);
    }

    [Fact]
    public async Task Repeated_set_keeps_original_snapshot_and_excludes_other_instances()
    {
        var directory = TestDirs.New("windows-proxy-repeat");
        var registry = new FakeRegistry(directory);
        using var first = Service(registry, directory);
        using var second = Service(registry, directory);
        await first.SetAsync("127.0.0.1", 2080);
        await Assert.ThrowsAsync<IOException>(() => second.ClearAsync());
        await first.SetAsync("127.0.0.1", 3080);
        Assert.Equal(new("127.0.0.1:3080", null), registry.Values["ProxyServer"]);
        await first.ClearAsync();
        Assert.Empty(registry.Values);
    }

    [Fact]
    public async Task Unwritable_journal_causes_no_registry_writes()
    {
        var directory = TestDirs.New("windows-proxy-journal-failure");
        var registry = new FakeRegistry(directory);
        using var blocked = new FileStream(registry.Journal + ".tmp", FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var service = Service(registry, directory);
        await Assert.ThrowsAnyAsync<IOException>(() => service.SetAsync("127.0.0.1", 2080));
        Assert.Equal(0, registry.Writes);
    }

    [Fact]
    public async Task Failed_notification_keeps_recovery_until_it_can_be_retried()
    {
        var directory = TestDirs.New("windows-proxy-notify");
        var registry = new FakeRegistry(directory);
        using (var first = Service(registry, directory)) { await first.SetAsync("127.0.0.1", 2080); }
        registry.FailNotify = true;
        using (var next = Service(registry, directory)) { await Assert.ThrowsAsync<IOException>(() => next.ClearAsync()); }
        Assert.Empty(registry.Values);
        Assert.True(File.Exists(registry.Journal));
        registry.FailNotify = false;
        using var retry = Service(registry, directory);
        await retry.ClearAsync();
        Assert.False(File.Exists(registry.Journal));
    }

    [Theory]
    [InlineData("{\"Version\":99,\"Changes\":[]}")]
    [InlineData("{\"Version\":1,\"Changes\":[null,null,null]}")]
    [InlineData("{\"Version\":1,\"Changes\":null}")]
    public async Task Corrupted_journal_is_retained_without_registry_writes(string journal)
    {
        var directory = TestDirs.New("windows-proxy-corrupt");
        var registry = new FakeRegistry(directory);
        await File.WriteAllTextAsync(registry.Journal, journal);
        using var service = Service(registry, directory);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ClearAsync());
        Assert.Equal(0, registry.Writes);
        Assert.True(File.Exists(registry.Journal));
    }

    [Fact]
    public async Task Safety_check_blocks_apply_and_startup_recovery()
    {
        var directory = TestDirs.New("windows-proxy-safety");
        var registry = new FakeRegistry(directory);
        using (var first = Service(registry, directory)) { await first.SetAsync("127.0.0.1", 2080); }
        using var locked = new WindowsProxyRecovery(registry, directory, () => throw new InvalidOperationException("locked"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => locked.SetAsync("127.0.0.1", 3080));
        await Assert.ThrowsAsync<InvalidOperationException>(() => locked.ClearAsync());
        Assert.Equal(3, registry.Writes);
        Assert.True(File.Exists(registry.Journal));
    }

    private static WindowsProxyRecovery Service(FakeRegistry registry, string directory) => new(registry, directory, () => { });

    private sealed class FakeRegistry(string directory) : IWindowsProxySettings
    {
        public Dictionary<string, WindowsProxyValue> Values { get; } = [];
        public string Journal => Path.Combine(directory, "windows-system-proxy.json");
        public int Writes { get; private set; }
        public int FailAfter { get; set; } = int.MaxValue;
        public bool FailNotify { get; set; }
        public WindowsProxyValue? Read(string name) => Values.GetValueOrDefault(name);
        public void Write(string name, WindowsProxyValue? value)
        {
            Assert.True(File.Exists(Journal));
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Journal));
            Assert.Equal(3, document.RootElement.GetProperty("Changes").GetArrayLength());
            if (Writes >= FailAfter) { throw new IOException("Simulated registry write failure."); }
            Writes++;
            if (value is null) { Values.Remove(name); }
            else { Values[name] = value; }
        }
        public void Notify()
        {
            if (FailNotify) { throw new IOException("Simulated notification failure."); }
        }
    }
}
