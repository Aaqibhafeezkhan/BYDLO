using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

public class TelegramProxyConnectionTests
{
    [Fact]
    public async Task Open_starts_before_launch_and_reuses_active_credentials_until_stopped()
    {
        var starts = 0;
        var store = new InMemorySecretStore();
        var secrets = new TgWsProxySecrets(store);
        var registry = new EngineRegistry(() => { starts++; return new MockEngine { TimeScale = 0.001 }; });
        await using var manager = Create(registry, store);
        var connection = new TelegramProxyConnection(manager, secrets);
        var settings = new AppSettings { MockMode = true };
        settings.TgWsProxy.Port = 1555;
        var secret = await secrets.GetOrCreateAsync();
        var expected = TgLinkBuilder.Build(settings.TgWsProxy.Host, 1555, secret, settings.TgWsProxy.FakeTlsDomain);
        var links = new List<string>();
        Task<bool> Launch(Uri uri)
        {
            Assert.Equal(ConnectionStatus.Connected, manager.TgWsProxy.Status);
            links.Add(uri.OriginalString);
            return Task.FromResult(true);
        }

        Assert.True(await connection.OpenAsync(settings, Launch));
        settings.TgWsProxy.Port = 1666;
        await secrets.RegenerateAsync();
        Assert.True(await connection.OpenAsync(settings, Launch));
        Assert.Equal(1, starts);
        Assert.Equal(new[] { expected, expected }, links);

        await manager.StopToolAsync(ConnectionManager.TgWsProxyKey);
        Assert.True(await connection.OpenAsync(settings, Launch));
        Assert.Equal(2, starts);
        Assert.Contains("port=1666", links[2], StringComparison.Ordinal);
        Assert.NotEqual(expected, links[2]);
    }

    [Fact]
    public async Task Concurrent_clicks_start_one_instance()
    {
        var starts = 0;
        var launches = 0;
        var store = new InMemorySecretStore();
        var secrets = new TgWsProxySecrets(store);
        await secrets.GetOrCreateAsync();
        var registry = new EngineRegistry(() => { starts++; return new MockEngine { TimeScale = 0.01 }; });
        await using var manager = Create(registry, store);
        var connection = new TelegramProxyConnection(manager, secrets);
        Task<bool> Launch(Uri _) { Interlocked.Increment(ref launches); return Task.FromResult(true); }
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => connection.OpenAsync(new AppSettings { MockMode = true }, Launch)));
        Assert.Equal(1, starts);
        Assert.Equal(5, launches);
    }

    [Fact]
    public async Task Failed_start_does_not_launch_Telegram_and_can_be_retried()
    {
        var store = new InMemorySecretStore();
        var registry = new EngineRegistry(() => new MockEngine { TimeScale = 0.001 });
        await using var manager = Create(registry, store);
        var connection = new TelegramProxyConnection(manager, new TgWsProxySecrets(store));
        var launches = 0;
        Task<bool> Launch(Uri _) { launches++; return Task.FromResult(true); }
        await Assert.ThrowsAsync<EngineException>(() => connection.OpenAsync(
            new AppSettings { MockMode = true, MockScenario = MockScenario.FailOnConnect }, Launch));
        Assert.Equal(0, launches);
        Assert.True(await connection.OpenAsync(new AppSettings { MockMode = true }, Launch));
        Assert.Equal(1, launches);
    }

    [Fact]
    public async Task Missing_Telegram_returns_false_and_keeps_proxy_available_for_retry()
    {
        var store = new InMemorySecretStore();
        var registry = new EngineRegistry(() => new MockEngine { TimeScale = 0.001 });
        await using var manager = Create(registry, store);
        var connection = new TelegramProxyConnection(manager, new TgWsProxySecrets(store));
        Assert.False(await connection.OpenAsync(new AppSettings { MockMode = true }, _ => Task.FromResult(false)));
        Assert.Equal(ConnectionStatus.Connected, manager.TgWsProxy.Status);
        Assert.True(await connection.OpenAsync(new AppSettings { MockMode = true }, _ => Task.FromResult(true)));
    }

    private static ConnectionManager Create(EngineRegistry registry, ISecretStore store) => new(registry, store,
        new RootedAppPaths(TestDirs.New("telegram-open")), new ConflictDetector(new FakePorts()),
        new LogHub(new LogRedactor()), os: OsFamily.Windows);
}
