using System.Net;
using Bydlo.Engines.SingBox;

namespace Bydlo.Tests;

public sealed class ClashConnectionsTests
{
    [Fact]
    public async Task Lists_and_closes_a_connection_using_the_authenticated_loopback_api()
    {
        using var http = new HttpClient(new Handler());
        var client = new ClashConnectionsClient(http, 9090, "fake-token");
        var connection = Assert.Single(await client.ListConnectionsAsync());
        Assert.Equal("example.invalid", connection.Host);
        Assert.Equal("browser", connection.Process);
        Assert.Equal("proxy", connection.Outbound);
        Assert.Equal(20, connection.DownloadBytes);
        await client.CloseConnectionAsync(connection.Id);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal(9090, request.RequestUri.Port);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-token", request.Headers.Authorization.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/connections", request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"connections":[{"id":"sample","metadata":{"host":"example.invalid","process":"browser"},
                        "chains":["proxy"],"download":20,"upload":10}]}
                        """),
                });
            }
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/connections/sample", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
