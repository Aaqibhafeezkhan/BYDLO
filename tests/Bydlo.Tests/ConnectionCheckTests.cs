using System.Net;
using System.Text;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public class ConnectionCheckTests
{
    [Theory]
    [InlineData(
        IpCheckProvider.IpWhoIs,
        "https://ipwho.is/",
        """{"ip":"203.0.113.7","success":true,"country":"United States","country_code":"US"}""",
        "203.0.113.7",
        "US",
        "United States")]
    [InlineData(
        IpCheckProvider.IpApiCo,
        "https://ipapi.co/json/",
        """{"ip":"198.51.100.9","country":"DE","country_name":"Germany","country_code":"DE"}""",
        "198.51.100.9",
        "DE",
        "Germany")]
    [InlineData(
        IpCheckProvider.IpInfo,
        "https://ipinfo.io/json",
        """{"ip":"192.0.2.10","country":"JP"}""",
        "192.0.2.10",
        "JP",
        null)]
    public async Task Check_parses_provider_response_through_fake_http(
        IpCheckProvider provider,
        string endpoint,
        string body,
        string ip,
        string countryCode,
        string? countryName)
    {
        int? seenPort = null;
        Uri? seenUri = null;
        var service = new ConnectionCheckService(port =>
        {
            seenPort = port;
            return new StubHandler(body, request => seenUri = request.RequestUri);
        });

        var result = await service.CheckAsync(provider, 2080);

        Assert.Equal(2080, seenPort);
        Assert.Equal(endpoint, seenUri!.ToString());
        Assert.Equal(ip, result.Ip);
        Assert.Equal(countryCode, result.CountryCode);
        Assert.Equal(countryName, result.CountryName);
        Assert.True(result.Latency >= TimeSpan.Zero);
    }

    [Fact]
    public async Task Http_error_is_reported()
    {
        var service = new ConnectionCheckService(_ => new StubHandler("{}", status: HttpStatusCode.BadGateway));

        var ex = await Assert.ThrowsAsync<ConnectionCheckException>(() => service.CheckAsync(IpCheckProvider.IpInfo, null));

        Assert.Contains("502", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"ip":"not-an-ip"}""")]
    public void Invalid_responses_are_rejected(string body) =>
        Assert.Throws<ConnectionCheckException>(() => ConnectionCheckService.ParseResponse(IpCheckProvider.IpInfo, body));

    [Fact]
    public void Provider_rejection_is_reported() =>
        Assert.Throws<ConnectionCheckException>(() =>
            ConnectionCheckService.ParseResponse(IpCheckProvider.IpWhoIs, """{"success":false,"message":"Reserved range"}"""));

    [Theory]
    [InlineData(IpCheckProvider.IpApiCo, """{"error":true,"reason":"RateLimited"}""")]
    [InlineData(IpCheckProvider.IpInfo, """{"error":{"title":"Rate limited","message":"Too many requests"}}""")]
    public void Provider_error_envelopes_are_reported(IpCheckProvider provider, string body) =>
        Assert.Throws<ConnectionCheckException>(() => ConnectionCheckService.ParseResponse(provider, body));

    [Fact]
    public void Error_false_is_not_treated_as_failure()
    {
        var result = ConnectionCheckService.ParseResponse(
            IpCheckProvider.IpApiCo,
            """{"ip":"203.0.113.7","country_code":"US","error":false}""");

        Assert.Equal("203.0.113.7", result.Ip);
        Assert.Equal("US", result.CountryCode);
    }

    [Theory]
    [InlineData(2080, true, "http://127.0.0.1:2080/")]
    [InlineData(2081, true, "http://127.0.0.1:2081/")]
    [InlineData(null, false, null)]
    public void Handler_uses_proxy_only_when_port_is_set(int? port, bool useProxy, string? proxyAddress)
    {
        using var handler = ConnectionCheckService.CreateHandler(port);

        Assert.Equal(useProxy, handler.UseProxy);
        if (proxyAddress is null)
        {
            Assert.Null(handler.Proxy);
            return;
        }

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal(new Uri(proxyAddress), proxy.Address);
    }

    [Theory]
    [InlineData(EngineKind.SingBox, ConnectionMode.LocalProxy, 2080, 2080)]
    [InlineData(EngineKind.Xray, ConnectionMode.LocalProxy, 2080, 2081)]
    [InlineData(EngineKind.SingBox, ConnectionMode.Tun, 2080, null)]
    [InlineData(EngineKind.WireGuard, ConnectionMode.LocalProxy, 2080, null)]
    [InlineData(EngineKind.Mock, ConnectionMode.LocalProxy, 2080, null)]
    public void Proxy_port_depends_on_engine_and_mode(EngineKind engine, ConnectionMode mode, int localPort, int? expected) =>
        Assert.Equal(expected, ConnectionCheckService.ResolveProxyPort(engine, mode, localPort));

    private sealed class StubHandler(string body, Action<HttpRequestMessage>? inspect = null, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
