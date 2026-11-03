using System.Net;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Engines.Xray;

namespace Bydlo.Tests;

public sealed class ServerSpeedTests
{
    private static readonly Uri Url = new("https://example.invalid/test");
    private static readonly Profile Profile = new() { Protocol = ProtocolKind.Trojan, Host = "test.invalid", Port = 443 };

    [Fact]
    public async Task Download_stops_at_the_byte_limit()
    {
        var stream = new MemoryStream(new byte[ServerSpeedProbe.MaximumBytes + 64]);
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(ServerSpeedProbe.MaximumBytes - 1, request.Headers.Range!.Ranges.Single().To);
            Assert.True(request.Headers.CacheControl!.NoCache);
            Assert.True(request.Headers.CacheControl.NoStore);
            Assert.Equal("identity", Assert.Single(request.Headers.AcceptEncoding).Value);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }));
        Assert.True(await ServerSpeedProbe.DownloadAsync(http, Url) > 0);
        Assert.Equal(ServerSpeedProbe.MaximumBytes, stream.PositionAfterDispose);
    }

    [Fact]
    public async Task Download_deadline_returns_the_speed_of_bytes_already_read()
    {
        using var stream = new StallingStream();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) })));
        Assert.True(await ServerSpeedProbe.DownloadAsync(http, Url, TimeSpan.FromMilliseconds(100)) > 0);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task User_cancellation_during_streaming_is_not_a_successful_partial_result()
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new StallingStream(cancellation.Cancel);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ServerSpeedProbe.DownloadAsync(http, Url, cancellation.Token));
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("http://example.invalid/test")]
    [InlineData("https://user:password@example.invalid/test")]
    [InlineData("/test")]
    public async Task Invalid_targets_are_rejected_before_starting_measurements(string target)
    {
        var probe = new ServerSpeedProbe((_, _, _) => throw new InvalidOperationException("Must not run"));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.RunAsync([Profile], new Uri(target, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public async Task Batch_does_not_hide_cleanup_failures_as_zero_speed()
    {
        var probe = new ServerSpeedProbe((_, _, _) => throw new EngineException(
            "Cleanup failed", localizationKey: "error.engine.cleanup"));
        var error = await Assert.ThrowsAsync<EngineException>(() => probe.RunAsync([Profile], Url));
        Assert.Equal("error.engine.cleanup", error.LocalizationKey);
    }

    [Fact]
    public async Task Batch_cancellation_waits_for_all_active_measurements_to_clean_up()
    {
        using var cancellation = new CancellationTokenSource();
        var active = 0;
        var started = 0;
        var probe = new ServerSpeedProbe(async (_, _, token) =>
        {
            Interlocked.Increment(ref active);
            try
            {
                if (Interlocked.Increment(ref started) == 2) { cancellation.Cancel(); }
                await Task.Delay(System.Threading.Timeout.Infinite, token);
                return 1L;
            }
            finally { Interlocked.Decrement(ref active); }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(
            Enumerable.Range(0, 6).Select(_ => new Profile()).ToArray(), Url, cancellation.Token));
        Assert.Equal(0, active);
        Assert.Equal(2, started);
    }

    private sealed class StallingStream(Action? afterFirstRead = null) : System.IO.MemoryStream(new byte[64])
    {
        public bool Disposed { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == 0)
            {
                var count = await base.ReadAsync(buffer, cancellationToken);
                afterFirstRead?.Invoke();
                return count;
            }
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Cancellation_stops_the_temporary_engine()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        }));
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        var engine = new XrayEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        var request = new EngineStartRequest
        {
            Profile = Profile,
            Secrets = new ProfileSecrets { Values = { [Keys.Password] = "fake" } },
            Settings = new AppSettings(),
            WorkDirectory = TestDirs.New("speed-test"),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ServerSpeedProbe.MeasureThroughEngineAsync(engine, request, http, Url, cancellation.Token));
        Assert.True(runner.Last!.Stopped);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    [Fact]
    public async Task Temporary_engine_cleanup_retries_a_failed_stop()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        var engine = new XrayEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        using var http = new HttpClient(new Handler((_, _) =>
        {
            runner.Last!.StopFailuresRemaining = 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }));
        var request = new EngineStartRequest
        {
            Profile = Profile,
            Secrets = new ProfileSecrets { Values = { [Keys.Password] = "fake" } },
            Settings = new AppSettings(),
            WorkDirectory = TestDirs.New("speed-stop-retry"),
        };
        Assert.True(await ServerSpeedProbe.MeasureThroughEngineAsync(engine, request, http, Url) > 0);
        Assert.True(runner.Last!.Stopped);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Failed_cleanup_remains_retryable_and_reports_a_localized_error()
    {
        var runner = new FakeProcessRunner { StartupOutput = { "Xray started" } };
        var engine = new XrayEngine(runner, new LogRedactor(), new FakeLocator(new PlatformInfo(OsFamily.Windows, CpuArch.X64)));
        using var http = new HttpClient(new Handler((_, _) =>
        {
            runner.Last!.StopError = new IOException("Fake persistent stop failure.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) });
        }));
        var request = new EngineStartRequest
        {
            Profile = Profile,
            Secrets = new ProfileSecrets { Values = { [Keys.Password] = "fake" } },
            Settings = new AppSettings(), WorkDirectory = TestDirs.New("speed-stop-failure"),
        };
        var error = await Assert.ThrowsAsync<EngineException>(() => ServerSpeedProbe.MeasureThroughEngineAsync(engine, request, http, Url));
        Assert.Equal("error.engine.cleanup", error.LocalizationKey);
        Assert.False(runner.Last!.Stopped);
        runner.Last.StopError = null;
        await ServerSpeedProbe.StopEngineAsync(engine);
        Assert.True(runner.Last.Stopped);
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Batch_limits_parallel_measurements_and_preserves_results()
    {
        var active = 0;
        var maximum = 0;
        var probe = new ServerSpeedProbe(async (_, _, token) =>
        {
            var count = Interlocked.Increment(ref active);
            Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), count));
            try { await Task.Delay(20, token); return 123L; }
            finally { Interlocked.Decrement(ref active); }
        });
        var profiles = Enumerable.Range(0, 5).Select(_ => new Profile()).ToArray();
        var results = await probe.RunAsync(profiles, Url);
        Assert.Equal(5, results.Count);
        Assert.All(results.Values, value => Assert.Equal(123L, value));
        Assert.InRange(maximum, 1, 2);
        Assert.Equal(0, active);
    }

    private sealed class MemoryStream(byte[] bytes) : System.IO.MemoryStream(bytes)
    {
        public long PositionAfterDispose { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (CanSeek)
            {
                PositionAfterDispose = Position;
            }
            base.Dispose(disposing);
        }
    }
}
