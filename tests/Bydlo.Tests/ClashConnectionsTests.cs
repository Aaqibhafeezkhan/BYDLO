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
        Assert.Empty(await client.ListConnectionsAsync());
    }

    [Fact]
    public async Task Reads_ip_process_path_rule_and_all_outbound_hops()
    {
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, """
            {"connections":[{"id":"id-1","metadata":{"host":"","destinationIP":"192.0.2.1",
            "process":"","processPath":"/usr/bin/browser"},"rule":"DomainSuffix","rulePayload":"example.invalid",
            "chains":["exit","entry"],"download":42,"upload":7},{"metadata":{}}]}
            """));
        var connection = Assert.Single(await new ClashConnectionsClient(http, 9090, "secret").ListConnectionsAsync());
        Assert.Equal("192.0.2.1", connection.Host);
        Assert.Equal("/usr/bin/browser", connection.Process);
        Assert.Equal("DomainSuffix: example.invalid", connection.Rule);
        Assert.Equal("exit → entry", connection.Outbound);
        Assert.Equal(42, connection.DownloadBytes);
        Assert.Equal(7, connection.UploadBytes);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../connections")]
    [InlineData("a/b")]
    [InlineData("%2e%2e")]
    [InlineData("")]
    public async Task Rejects_ids_that_could_address_other_api_paths(string id)
    {
        using var http = new HttpClient(new Handler());
        await Assert.ThrowsAsync<ArgumentException>(() => new ClashConnectionsClient(http, 9090, "secret").CloseConnectionAsync(id));
    }

    [Fact]
    public async Task Closing_an_already_closed_connection_succeeds()
    {
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.NotFound));
        await new ClashConnectionsClient(http, 9090, "secret").CloseConnectionAsync("gone-id");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Surfaces_api_failures(HttpStatusCode status)
    {
        using var http = new HttpClient(new ResponseHandler(status));
        var client = new ClashConnectionsClient(http, 9090, "secret");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListConnectionsAsync());
        await Assert.ThrowsAsync<HttpRequestException>(() => client.CloseConnectionAsync("id"));
    }

    [Fact]
    public async Task Rejects_an_unauthenticated_api_client()
    {
        using var http = new HttpClient(new Handler());
        await Assert.ThrowsAsync<ArgumentException>(() => new ClashConnectionsClient(http, 9090, "").ListConnectionsAsync());
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"connections\":42}")]
    public async Task Surfaces_invalid_payloads(string json)
    {
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, json));
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => new ClashConnectionsClient(http, 9090, "secret").ListConnectionsAsync());
    }

    private sealed class ResponseHandler(HttpStatusCode status, string json = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
    }

    private sealed class Handler : HttpMessageHandler
    {
        private bool _closed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("127.0.0.1", request.RequestUri!.Host);
            Assert.Equal(9090, request.RequestUri.Port);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-token", request.Headers.Authorization.Parameter);
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/connections", request.RequestUri.AbsolutePath);
                if (_closed)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"connections\":[]}") });
                }
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
            _closed = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
