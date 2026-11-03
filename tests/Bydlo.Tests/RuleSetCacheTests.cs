using System.Net;
using System.Security.Cryptography;
using Bydlo.Core.Routing;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public sealed class RuleSetCacheTests
{
    private static readonly byte[] ValidBytes = [1, 2, 3];
    private static readonly RuleSetSource Source = new("test", new Uri("https://example.test/rules.srs"),
        Convert.ToHexStringLower(SHA256.HashData(ValidBytes)), 3);

    [Fact]
    public async Task Publishes_verified_content_and_preserves_it_after_failed_update()
    {
        using var handler = new Stub { Data = ValidBytes };
        using var http = new HttpClient(handler);
        var cache = new RuleSetCache(TestDirs.New("rule-set"), http);

        var path = await cache.UpdateAsync(Source);
        Assert.Equal(cache.GetPath(Source), path);
        handler.Data = [1, 2, 4];

        await Assert.ThrowsAsync<IntegrityException>(() => cache.UpdateAsync(Source));
        Assert.Equal(ValidBytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task Rejects_oversized_stream_even_without_content_length()
    {
        using var handler = new Stub { Data = [1, 2, 3, 4] };
        using var http = new HttpClient(handler);
        var cache = new RuleSetCache(TestDirs.New("rule-set"), http);

        await Assert.ThrowsAsync<InvalidDataException>(() => cache.UpdateAsync(Source));
        Assert.False(File.Exists(cache.GetPath(Source)));
    }

    private sealed class Stub : HttpMessageHandler
    {
        public required byte[] Data { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StreamContent(new UnseekableStream(Data)),
            });
    }

    [Fact]
    public async Task Http_failure_and_cancellation_leave_verified_cache_unchanged()
    {
        using var handler = new Stub { Data = ValidBytes };
        using var http = new HttpClient(handler);
        var cache = new RuleSetCache(TestDirs.New("rule-set-failure"), http);
        var path = await cache.UpdateAsync(Source);
        handler.Status = HttpStatusCode.NotFound;
        await Assert.ThrowsAsync<HttpRequestException>(() => cache.UpdateAsync(Source));
        handler.Status = HttpStatusCode.OK;
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.UpdateAsync(Source, ct.Token));
        Assert.Equal(ValidBytes, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task New_hash_does_not_replace_previous_version_on_failed_download()
    {
        using var handler = new Stub { Data = ValidBytes };
        using var http = new HttpClient(handler);
        var cache = new RuleSetCache(TestDirs.New("rule-set-version"), http);
        var path = await cache.UpdateAsync(Source);
        var changed = Source with { Sha256 = new string('a', 64) };
        await Assert.ThrowsAsync<IntegrityException>(() => cache.UpdateAsync(changed));
        Assert.Equal(ValidBytes, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(cache.GetPath(changed)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
