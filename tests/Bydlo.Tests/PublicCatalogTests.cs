using System.Net;
using System.Text;
using Bydlo.Core.Import;

namespace Bydlo.Tests;

/// <summary>Issue #39: built-in public subscription catalog and mirror fallback.</summary>
public class PublicCatalogTests
{
    [Fact]
    public void Catalog_has_unique_ids_and_https_mirrors_only()
    {
        var sources = PublicSubscriptionCatalog.Sources;
        Assert.NotEmpty(sources);
        Assert.Equal(sources.Count, sources.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var source in sources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source.NameKey));
            Assert.False(string.IsNullOrWhiteSpace(source.DescriptionKey));
            Assert.True(source.Urls.Count >= 2, $"Source {source.Id} needs a primary URL plus mirrors.");
            Assert.Equal(source.Urls.Count, source.Urls.Distinct(StringComparer.Ordinal).Count());
            foreach (var url in source.Urls)
            {
                Assert.True(Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https",
                    $"Source {source.Id} has a non-HTTPS URL: {url}");
            }
        }
    }

    [Fact]
    public async Task FetchFirst_tries_mirrors_in_order_and_returns_working_url()
    {
        var good = Convert.ToBase64String(Encoding.UTF8.GetBytes("vless://u@h.example.com:443#1"));
        using var http = new HttpClient(new MapHandler(new Dictionary<string, HttpResponseMessage>
        {
            ["https://one.invalid/s"] = new(HttpStatusCode.InternalServerError),
            ["https://two.invalid/s"] = new(HttpStatusCode.OK) { Content = new StringContent(good) },
        }));
        var client = new SubscriptionClient(http, new ProfileImporter());
        var (result, url) = await client.FetchFirstAsync(["https://one.invalid/s", "https://two.invalid/s"]);
        Assert.False(result.IsEmpty);
        Assert.Equal("https://two.invalid/s", url);
    }

    [Fact]
    public async Task FetchFirst_returns_last_failure_when_all_mirrors_fail()
    {
        using var http = new HttpClient(new MapHandler(new Dictionary<string, HttpResponseMessage>
        {
            ["https://one.invalid/s"] = new(HttpStatusCode.BadGateway),
        }));
        var client = new SubscriptionClient(http, new ProfileImporter());
        var (result, url) = await client.FetchFirstAsync(["ftp://one.invalid/s", "https://one.invalid/s"]);
        Assert.True(result.IsEmpty);
        Assert.Null(url);
        Assert.NotEmpty(result.Errors);
    }

    private sealed class MapHandler(Dictionary<string, HttpResponseMessage> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri?.ToString() ?? "";
            return Task.FromResult(responses.TryGetValue(key, out var response) ? response : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
