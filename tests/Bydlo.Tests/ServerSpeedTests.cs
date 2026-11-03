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
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        }));
        Assert.True(await ServerSpeedProbe.DownloadAsync(http, Url) > 0);
        Assert.Equal(ServerSpeedProbe.MaximumBytes, stream.PositionAfterDispose);
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
