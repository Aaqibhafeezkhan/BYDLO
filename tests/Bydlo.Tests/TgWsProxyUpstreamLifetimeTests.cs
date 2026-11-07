using System.Collections.Concurrent;
using System.Text;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

public partial class TgWsProxyTests
{
    [Theory]
    [InlineData("tcp", false)]
    [InlineData("tcp", true)]
    [InlineData("cf", false)]
    [InlineData("cf", true)]
    [InlineData("pool", false)]
    [InlineData("pool", true)]
    public async Task Upstream_is_closed_when_relay_init_write_or_flush_fails(string route, bool failFlush)
    {
        var (server, network) = await LifetimeServerAsync(route, failFlush, block: false);
        await using var proxy = server;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new ReplayClientStream(telegram.Init);
        await proxy.HandleClientAsync(client, "init-failure").WaitAsync(TimeSpan.FromSeconds(5));

        var upstream = Assert.Single(network.Streams, s => s.RelayAttempted);
        Assert.True(upstream.Disposed, "The upstream must be released before the client handler returns.");
        Assert.Equal(0, proxy.Stats.ActiveConnections);
        await proxy.DisposeAsync();
        Assert.All(network.Streams, s => Assert.True(s.Disposed));
    }

    [Theory]
    [InlineData("tcp")]
    [InlineData("cf")]
    [InlineData("pool")]
    public async Task Upstream_is_closed_when_shutdown_cancels_relay_init(string route)
    {
        var (server, network) = await LifetimeServerAsync(route, failFlush: false, block: true);
        await using var proxy = server;
        using var telegram = new TelegramClient(Secret, MtProto.Intermediate, 2);
        using var client = new ReplayClientStream(telegram.Init);
        var handling = proxy.HandleClientAsync(client, "init-cancel");
        await network.InitEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await proxy.DisposeAsync();
        await handling.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(network.Streams, s => Assert.True(s.Disposed));
        Assert.Equal(0, proxy.Stats.ActiveConnections);
    }

    private static async Task<(TgWsProxyServer Server, LifetimeNetwork Network)> LifetimeServerAsync(string route, bool failFlush, bool block)
    {
        var network = new LifetimeNetwork(route != "tcp", failFlush, block);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refilled = 0;
        var options = new TgWsProxyOptions
        {
            Secret = Secret, PoolSize = route == "pool" ? 1 : 0,
            CfProxyFallback = route == "cf", CfProxyDomains = ["example.com"], Verbose = true,
            DcRedirects = route == "pool" ? new Dictionary<int, string> { [2] = "192.0.2.1" } : new Dictionary<int, string>(),
        };
        var server = new TgWsProxyServer(options, network, (_, message) =>
        {
            if (message.StartsWith("WS pool refilled", StringComparison.Ordinal) && Interlocked.Increment(ref refilled) == 2)
            {
                ready.TrySetResult();
            }
        });
        if (route == "pool")
        {
            server.Pool.Warmup();
            try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { await server.DisposeAsync(); throw; }
        }

        return (server, network);
    }

    private sealed class LifetimeNetwork(bool websocket, bool failFlush, bool block) : ITgNetwork
    {
        public ConcurrentBag<LifetimeStream> Streams { get; } = [];
        public TaskCompletionSource InitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct) => Open();
        public Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct) => Open();
        private Task<Stream> Open()
        {
            var stream = new LifetimeStream(websocket, failFlush, block, InitEntered);
            Streams.Add(stream);
            return Task.FromResult<Stream>(stream);
        }
        public ITgListener Listen(string host, int port) => throw new NotSupportedException("No listeners in lifetime tests.");
        public Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class LifetimeStream(bool websocket, bool failFlush, bool block, TaskCompletionSource initEntered) : MemoryStream
    {
        private readonly MemoryStream _head = new(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n\r\n"));
        public bool RelayAttempted { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (websocket && _head.Position < _head.Length)
            {
                return await _head.ReadAsync(buffer, cancellationToken);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // GET and close/pong writes are not the initial MTProto binary frame.
            if (websocket && (buffer.IsEmpty || buffer.Span[0] != 0x82)) { return; }
            RelayAttempted = true;
            initEntered.TrySetResult();
            if (block) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            if (!failFlush) { throw new IOException("Injected relay-init write failure."); }
        }
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            RelayAttempted && failFlush ? Task.FromException(new IOException("Injected relay-init flush failure.")) : Task.CompletedTask;
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Disposed = true; _head.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
