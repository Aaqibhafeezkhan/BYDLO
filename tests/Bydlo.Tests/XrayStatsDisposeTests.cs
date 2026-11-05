using System.Net;
using System.Reflection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.Xray;
using Grpc.Core;
using Grpc.Net.Client;

namespace Bydlo.Tests;

/// <summary>Issue #347: the stats channel must own and release its HTTP handler. No sockets, no processes.</summary>
public sealed class XrayStatsDisposeTests
{
    private sealed class TrackedHandler : HttpMessageHandler
    {
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new RpcException(new Status(StatusCode.Unavailable, "fake-stats")));

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public void Channel_with_ownership_disposes_handler()
    {
        var handler = new TrackedHandler();
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
        channel.Dispose();
        Assert.True(handler.Disposed);
    }

    [Fact]
    public void Channel_without_ownership_leaves_handler_alive()
    {
        // Documents the #347 root cause: Grpc.Net.Client 2.85.0 does not dispose
        // the handler by default, so the engine must opt into ownership.
        var handler = new TrackedHandler();
        using var channel = GrpcChannel.ForAddress("http://127.0.0.1:1", new GrpcChannelOptions
        {
            HttpHandler = handler,
        });
        channel.Dispose();
        Assert.False(handler.Disposed);
        handler.Dispose();
    }

    private static XrayEngine NewEngine() => new(
        new FakeProcessRunner(),
        new LogRedactor(),
        new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));

    private static void SetStatsPort(XrayEngine engine, int port) =>
        typeof(XrayEngine).GetField("_statsPort", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(engine, port);

    private static Task InvokeOnStartedAsync(XrayEngine engine, EngineStartRequest request)
    {
        var method = typeof(XrayEngine).GetMethod("OnStartedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(engine, [request, CancellationToken.None])!;
    }

    private static Task InvokeOnStoppingAsync(XrayEngine engine)
    {
        var method = typeof(XrayEngine).GetMethod("OnStoppingAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)method.Invoke(engine, [])!;
    }

    [Fact]
    public async Task Stop_releases_owned_stats_handler_and_waits_loop()
    {
        var engine = NewEngine();
        var tracked = new TrackedHandler();
        engine.StatsHandlerFactoryForTests = () => tracked;
        SetStatsPort(engine, 1);

        var request = new EngineStartRequest
        {
            Profile = new Profile { Protocol = ProtocolKind.Vless, Host = "vpn.invalid", Port = 443 },
            Secrets = new ProfileSecrets(),
            Settings = new AppSettings(),
            WorkDirectory = TestDirs.New("xray-stats-dispose"),
        };

        await InvokeOnStartedAsync(engine, request);
        Assert.Same(tracked, engine.StatsHandlerForTests);
        Assert.NotNull(engine.StatsChannelForTests);
        var loop = engine.TrafficTaskForTests;
        Assert.NotNull(loop);

        await InvokeOnStoppingAsync(engine);

        Assert.True(tracked.Disposed);
        Assert.Null(engine.StatsHandlerForTests);
        Assert.Null(engine.StatsChannelForTests);
        Assert.Null(engine.TrafficTaskForTests);
        Assert.True(loop.IsCompleted);

        // Idempotent: a second stop must not throw or resurrect state.
        await InvokeOnStoppingAsync(engine);
        Assert.True(tracked.Disposed);
    }

    [Fact]
    public async Task Stop_without_start_is_safe()
    {
        var engine = NewEngine();
        await InvokeOnStoppingAsync(engine);
        await InvokeOnStoppingAsync(engine);
        Assert.Null(engine.StatsHandlerForTests);
        Assert.Null(engine.StatsChannelForTests);
    }
}
