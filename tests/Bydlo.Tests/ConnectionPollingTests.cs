using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>
/// Issue #220: polling the Clash API must snapshot the engine and query it outside the
/// connection gate, so a stuck request never blocks Connect/Disconnect/Close.
/// </summary>
public class ConnectionPollingTests
{
    private sealed class GatedEngine(MockEngine inner, TaskCompletionSource listGate, TaskCompletionSource closeGate, Exception? listFault = null)
        : IEngine, IActiveConnectionSource
    {
        public async Task<IReadOnlyList<ActiveConnection>> ListConnectionsAsync(CancellationToken ct = default)
        {
            if (listFault is not null)
            {
                throw listFault;
            }

            await listGate.Task.WaitAsync(ct).ConfigureAwait(false);
            return await inner.ListConnectionsAsync(ct).ConfigureAwait(false);
        }

        public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
        {
            await closeGate.Task.WaitAsync(ct).ConfigureAwait(false);
            await inner.CloseConnectionAsync(id, ct).ConfigureAwait(false);
        }

        public EngineDescriptor Descriptor => inner.Descriptor;
        public EngineState State => inner.State;
        public event EventHandler<EngineStateChangedEventArgs>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public event EventHandler<string>? Log
        {
            add => inner.Log += value;
            remove => inner.Log -= value;
        }

        public event EventHandler<TrafficSample>? Traffic
        {
            add => inner.Traffic += value;
            remove => inner.Traffic -= value;
        }

        public bool CanHandle(Profile profile) => inner.CanHandle(profile);

        public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            inner.CheckAvailabilityAsync(settings, ct);

        public Task StartAsync(EngineStartRequest request, CancellationToken ct = default) =>
            inner.StartAsync(request, ct);

        public Task StopAsync(CancellationToken ct = default) => inner.StopAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ConnectionManager Create(Func<IEngine> factory)
    {
        var manager = new ConnectionManager(new EngineRegistry(factory), new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("poll")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows)
        {
            ReconnectDelay = _ => TimeSpan.FromMilliseconds(10),
        };
        return manager;
    }

    private static Profile Demo() => new() { Name = "demo", Protocol = ProtocolKind.Vless, Host = "demo.invalid", Port = 443 };

    [Fact]
    public async Task Listing_connections_does_not_block_disconnect()
    {
        var listGate = Gate();
        var closeGate = Gate();
        closeGate.SetResult();
        await using var manager = Create(() => new GatedEngine(new MockEngine(seed: 1) { TimeScale = 0.01 }, listGate, closeGate));
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });

        var listTask = manager.ListActiveConnectionsAsync();
        try
        {
            await Task.Delay(200);
            var disconnectTask = manager.DisconnectAsync();
            var finished = await Task.WhenAny(disconnectTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(disconnectTask, finished);
            await disconnectTask;
        }
        finally
        {
            // A regression that reintroduces the gate must fail this test, never hang it.
            listGate.TrySetResult();
        }

        var connections = Assert.Single((await listTask)!);
        Assert.Equal("mock-1", connections.Id);
    }

    [Fact]
    public async Task Closing_connection_does_not_block_listing()
    {
        var listGate = Gate();
        var closeGate = Gate();
        listGate.SetResult();
        await using var manager = Create(() => new GatedEngine(new MockEngine(seed: 2) { TimeScale = 0.01 }, listGate, closeGate));
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });

        var closeTask = manager.CloseActiveConnectionAsync("mock-1");
        try
        {
            await Task.Delay(200);
            var listTask = manager.ListActiveConnectionsAsync();
            var finished = await Task.WhenAny(listTask, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.Same(listTask, finished);
        }
        finally
        {
            closeGate.TrySetResult();
        }

        await closeTask;
        Assert.Empty((await manager.ListActiveConnectionsAsync())!);
    }

    [Fact]
    public async Task Listing_cancelled_while_stuck_throws_cancel()
    {
        var listGate = Gate();
        var closeGate = Gate();
        await using var manager = Create(() => new GatedEngine(new MockEngine(seed: 3) { TimeScale = 0.01 }, listGate, closeGate));
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.ListActiveConnectionsAsync(cts.Token));
        listGate.SetResult();
    }

    [Fact]
    public async Task Engine_failure_surfaces_as_request_error()
    {
        var listGate = Gate();
        var closeGate = Gate();
        await using var manager = Create(() => new GatedEngine(new MockEngine(seed: 4) { TimeScale = 0.01 }, listGate, closeGate,
            listFault: new HttpRequestException("controller gone")));
        await manager.ConnectAsync(Demo(), new AppSettings { MockMode = true });

        await Assert.ThrowsAsync<HttpRequestException>(() => manager.ListActiveConnectionsAsync());
    }

    [Fact]
    public void Traffic_stream_checks_status_and_stops_quietly()
    {
        var root = RepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Engines", "SingBox", "SingBoxEngine.cs"));
        Assert.Contains("resp.EnsureSuccessStatusCode()", code, StringComparison.Ordinal);
        Assert.Contains("or InvalidOperationException or FormatException", code, StringComparison.Ordinal);
        Assert.Contains("UseProxy = false", code, StringComparison.Ordinal);
        Assert.Contains("PauseAsync(ct)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Loopback_controllers_bypass_the_system_proxy()
    {
        var root = RepositoryRoot();
        var engine = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Engines", "SingBox", "SingBoxEngine.cs"));
        Assert.Contains("private static readonly HttpClient ClashHttp = new(new SocketsHttpHandler { UseProxy = false", engine, StringComparison.Ordinal);
        var xray = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Engines", "Xray", "XrayEngine.cs"));
        Assert.Contains("UseProxy = false", xray, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_loopback_clients_keep_the_system_proxy()
    {
        // The speed probe measures through the engine proxy on purpose; update/notice/connectivity
        // clients must reach the internet and therefore must not bypass the proxy (see #220).
        var root = RepositoryRoot();
        var speed = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Core", "Connection", "ConnectionManager.Speed.cs"));
        Assert.Contains("Proxy = new WebProxy", speed, StringComparison.Ordinal);
        foreach (var relative in new[]
        {
            Path.Combine("src", "Bydlo.Core", "Diagnostics", "UpdateChecker.cs"),
            Path.Combine("src", "Bydlo.Core", "Diagnostics", "ProjectNotice.cs"),
            Path.Combine("src", "Bydlo.Core", "Diagnostics", "GitHubContributors.cs"),
            Path.Combine("src", "Bydlo.Core", "Diagnostics", "ConnectionCheckService.cs"),
            Path.Combine("src", "Bydlo.Engines", "TgWsProxy", "TgNetwork.cs"),
            Path.Combine("src", "Bydlo.App", "Services", "AppServices.cs"),
        })
        {
            var code = File.ReadAllText(Path.Combine(root, relative));
            Assert.DoesNotContain("UseProxy = false", code, StringComparison.Ordinal);
        }
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
