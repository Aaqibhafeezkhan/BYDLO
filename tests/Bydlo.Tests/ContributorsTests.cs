using System.Net;
using System.Text.Json;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class ContributorsTests
{
    private const string Response = """
        [
          {"login":"alice","type":"User","contributions":12,"html_url":"https://evil.example/alice"},
          {"login":"github-actions[bot]","type":"Bot","contributions":40},
          {"login":"bob","type":"User","contributions":30},
          {"login":"-bad-","type":"User","contributions":99},
          {"login":"carol","type":"Organization","contributions":5},
          {"login":"Alice","type":"User","contributions":3},
          {"type":"User","contributions":7},
          "garbage"
        ]
        """;

    [Fact]
    public void Parser_keeps_users_with_valid_logins_ordered_by_contributions()
    {
        var list = GitHubContributorsClient.ParseContributors(Response);
        Assert.Equal(["bob", "alice"], list.Select(c => c.Login));
        Assert.Equal(12, list[1].Contributions);
        // Profile links come from the login, never from the response.
        Assert.Equal("https://github.com/alice", list[1].ProfileUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"message":"API rate limit exceeded"}""")]
    public void Parser_returns_empty_for_unexpected_payloads(string json)
        => Assert.Empty(GitHubContributorsClient.ParseContributors(json));

    [Theory]
    [InlineData("endercodezz", true)]
    [InlineData("a-b-c", true)]
    [InlineData("a--b", false)]
    [InlineData("-a", false)]
    [InlineData("a-", false)]
    [InlineData("name/../x", false)]
    [InlineData("1234567890123456789012345678901234567890", false)]
    public void Login_validation_follows_github_rules(string login, bool valid)
        => Assert.Equal(valid, GitHubContributorsClient.IsValidLogin(login));

    [Fact]
    public async Task Refresh_caches_list_survives_restart_and_throttles_requests()
    {
        var requests = 0;
        var refresher = new ContributorsRefresher(Client(() =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Response) };
        }));
        var settings = new AppSettings();
        Assert.Equal(2, (await refresher.RefreshAsync(settings, false)).Count);
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(["bob", "alice"], (await refresher.RefreshAsync(settings, false)).Select(c => c.Login));
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_checks_or_blocked_network_never_call_http(bool enabled, bool blocked)
    {
        var refresher = new ContributorsRefresher(new GitHubContributorsClient(() =>
            throw new Xunit.Sdk.XunitException("HTTP must not be created")));
        var cached = new Contributor("bob", 1);
        var settings = new AppSettings { CheckForUpdates = enabled, Contributors = [cached] };
        Assert.Equal([cached], await refresher.RefreshAsync(settings, blocked));
        Assert.Null(settings.LastContributorsCheck);
    }

    [Fact]
    public async Task Offline_or_empty_response_keeps_cache_and_retries_after_a_day()
    {
        var requests = 0;
        var refresher = new ContributorsRefresher(Client(() =>
        {
            requests++;
            return requests == 1
                ? throw new HttpRequestException("Offline")
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        var cached = new Contributor("bob", 1);
        var settings = new AppSettings { Contributors = [cached] };
        Assert.Equal([cached], await refresher.RefreshAsync(settings, false));
        Assert.Equal([cached], await refresher.RefreshAsync(settings, false));
        Assert.Equal(1, requests);
        settings.LastContributorsCheck = DateTimeOffset.UtcNow.AddHours(-25);
        Assert.Equal([cached], await refresher.RefreshAsync(settings, false));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Rate_limited_response_keeps_cache()
    {
        var refresher = new ContributorsRefresher(Client(() => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var cached = new Contributor("bob", 1);
        var settings = new AppSettings { Contributors = [cached] };
        Assert.Equal([cached], await refresher.RefreshAsync(settings, false));
        Assert.NotNull(settings.LastContributorsCheck);
    }

    [Fact]
    public void Profile_url_is_not_serialized()
        => Assert.DoesNotContain("ProfileUrl", JsonSerializer.Serialize(new Contributor("bob", 1)), StringComparison.Ordinal);

    private static GitHubContributorsClient Client(Func<HttpResponseMessage> send)
        => new(() => new FakeHandler(send));

    private sealed class FakeHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(GitHubContributorsClient.ContributorsUrl, request.RequestUri?.ToString());
            return Task.FromResult(send());
        }
    }
}
