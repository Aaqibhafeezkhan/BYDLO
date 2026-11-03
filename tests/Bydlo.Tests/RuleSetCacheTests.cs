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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new UnseekableStream(Data)),
            });
    }

    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
