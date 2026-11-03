using System.Net;
using Bydlo.Core.Import;

namespace Bydlo.Tests;

public sealed class SubscriptionTimeoutTests
{
    [Fact]
    public async Task Preview_times_out_while_reading_body_and_disposes_stream()
    {
        using var stream = new BlockingStream();
        using var handler = new SubscriptionHandler(stream);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new SubscriptionClient(http, new ProfileImporter());

        var error = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            client.FetchPreviewAsync("https://slow.invalid/sub").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsType<TimeoutException>(error.InnerException);
        Assert.True(stream.ReadStarted.Task.IsCompletedSuccessfully);
        Assert.True(stream.Disposed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_timeout_tries_next_mirror(bool refresh)
    {
        var source = PublicSubscriptionCatalog.Sources[0];
        using var stream = new BlockingStream();
        using var handler = new SubscriptionHandler(stream);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        var client = new SubscriptionClient(http, new ProfileImporter());

        if (refresh)
        {
            var preview = await client.FetchRefreshAsync(source.Urls[0]).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(preview.Import.Profiles);
        }
        else
        {
            var (preview, url) = await client.FetchFirstAsync(source.Urls).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(preview.Import.Profiles);
            Assert.Equal(source.Urls[1], url);
        }

        Assert.Equal(source.Urls.Take(2), handler.Requests);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_stops_body_without_trying_next_mirror(bool infiniteTimeout)
    {
        using var stream = new BlockingStream();
        using var handler = new SubscriptionHandler(stream);
        using var http = new HttpClient(handler)
        {
            Timeout = infiniteTimeout ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(30),
        };
        using var cancellation = new CancellationTokenSource();
        var client = new SubscriptionClient(http, new ProfileImporter());
        var fetch = client.FetchFirstAsync(["https://slow.invalid/sub", "https://good.invalid/sub"], cancellation.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.IsNotType<TimeoutException>(error.InnerException);
        Assert.Single(handler.Requests);
        Assert.True(stream.Disposed);
    }

    private sealed class SubscriptionHandler(Stream firstBody) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Requests.Count == 1
                    ? new StreamContent(firstBody)
                    : new StringContent("trojan://fixture@node.invalid:443"),
            });
        }
    }

    private sealed class BlockingStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
