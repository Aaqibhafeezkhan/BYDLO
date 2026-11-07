using System.Security.Cryptography;
using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

public partial class TgWsProxyTests
{
    [Fact]
    public async Task Replayed_hello_is_masked_across_server_instances()
    {
        // Exercise the production process-wide cache without sharing state with another test's random hello.
        var hello = FakeTlsClientHello(Secret, DateTimeOffset.UtcNow);
        var first = await ProcessHelloAsync(hello);
        Assert.Equal(FakeTls.RecordHandshake, first.Response[0]);
        Assert.Empty(first.MaskingHosts);

        var second = await ProcessHelloAsync(hello);
        Assert.Empty(second.Response);
        Assert.Equal(["example.com"], second.MaskingHosts);
        Assert.Equal(hello, second.ForwardedHello);
    }

    [Fact]
    public async Task Invalid_hmac_does_not_poison_replay_cache()
    {
        var clock = new ReplayClock();
        var cache = new FakeTlsReplayCache(clock);
        var hello = FakeTlsClientHello(Secret, clock.UtcNow);
        var invalid = (byte[])hello.Clone();
        // Keep ClientRandom identical but invalidate the HMAC by changing the session ID.
        invalid[FakeTls.SessionIdOffset] ^= 1;
        Assert.Empty((await ProcessHelloAsync(invalid, clock, cache)).Response);
        Assert.Equal(FakeTls.RecordHandshake, (await ProcessHelloAsync(hello, clock, cache)).Response[0]);
    }

    [Fact]
    public async Task Expired_timestamp_does_not_poison_replay_cache()
    {
        var clock = new ReplayClock();
        var cache = new FakeTlsReplayCache(clock);
        var hello = FakeTlsClientHello(Secret, clock.UtcNow);
        var original = clock.UtcNow;
        clock.UtcNow += TimeSpan.FromMinutes(5);
        Assert.Empty((await ProcessHelloAsync(hello, clock, cache)).Response);
        clock.UtcNow = original;
        Assert.Equal(FakeTls.RecordHandshake, (await ProcessHelloAsync(hello, clock, cache)).Response[0]);
    }

    [Fact]
    public async Task Replay_stays_masked_for_the_entire_hello_validity_window()
    {
        var clock = new ReplayClock();
        var helloTime = clock.UtcNow;
        var hello = FakeTlsClientHello(Secret, helloTime);
        var cache = new FakeTlsReplayCache(clock);
        clock.UtcNow = helloTime.AddSeconds(-FakeTls.TimestampTolerance);
        Assert.Equal(FakeTls.RecordHandshake, (await ProcessHelloAsync(hello, clock, cache)).Response[0]);
        clock.Advance(TimeSpan.FromSeconds(2 * FakeTls.TimestampTolerance + 0.999));
        Assert.NotNull(FakeTls.VerifyClientHello(hello, Secret, clock.UtcNow));
        Assert.Empty((await ProcessHelloAsync(hello, clock, cache)).Response);
    }

    private static async Task<(byte[] Response, string[] MaskingHosts, byte[] ForwardedHello)> ProcessHelloAsync(
        byte[] hello, TimeProvider? clock = null, FakeTlsReplayCache? cache = null)
    {
        var network = new MaskingNetwork();
        var options = new TgWsProxyOptions { Secret = Secret, FakeTlsDomain = "example.com", PoolSize = 0, CfProxyFallback = false };
        await using var server = cache is null
            ? new TgWsProxyServer(options, network, (_, _) => { }, clock)
            : new TgWsProxyServer(options, network, (_, _) => { }, clock, cache);
        using var client = new ReplayClientStream(hello);
        // EOF after the TLS hello ends the first, accepted handshake before any Telegram connection.
        await server.HandleClientAsync(client, "replay-test").WaitAsync(TimeSpan.FromSeconds(5));
        return (client.Output.ToArray(), network.Hosts.ToArray(), network.Upstream.ToArray());
    }

    private sealed class MaskingNetwork : ITgNetwork
    {
        public List<string> Hosts { get; } = [];
        public MemoryStream Upstream { get; } = new();
        public Task<Stream> ConnectAsync(string host, int port, CancellationToken ct)
        {
            Hosts.Add(host);
            return Task.FromResult<Stream>(Upstream);
        }
        public Task<Stream> ConnectTlsAsync(string host, int port, string serverName, bool validateCertificate, CancellationToken ct) =>
            throw new NotSupportedException();
        public ITgListener Listen(string host, int port) => throw new NotSupportedException("No real listener in replay tests.");
        public Task<string?> GetStringAsync(Uri uri, IReadOnlyDictionary<string, string> pinnedHosts, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ReplayClientStream(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        public MemoryStream Output { get; } = new();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => Output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => Output.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _input.Dispose(); Output.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
