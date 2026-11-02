using System.Net;
using System.Text;
using Bydlo.Core.Diagnostics;

namespace Bydlo.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.1.0", "v0.2.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.2.0", "v0.1.9", false)]
    [InlineData("1.14.3", "v1.14.4", true)]
    [InlineData("1.14.3", "v1.14.3", false)]
    [InlineData("0.1", "v0.1.0", false)]
    [InlineData("0.1.0", "v0.1.0-beta", false)]
    public void Version_compare_handles_v_prefix(string current, string tag, bool expected) =>
        Assert.Equal(expected, GitHubUpdateClient.IsNewer(current, tag));

    [Fact]
    public void Parse_release_reads_tag_and_url()
    {
        var release = GitHubUpdateClient.ParseRelease("""{"tag_name":"v0.2.0","html_url":"https://github.com/endercodezz/BYDLO/releases/tag/v0.2.0"}""");
        Assert.NotNull(release);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Contains("releases/tag/v0.2.0", release.HtmlUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"name":"no tag"}""")]
    public void Parse_release_rejects_bad_payloads(string json) =>
        Assert.Null(GitHubUpdateClient.ParseRelease(json));

    [Fact]
    public async Task Bydlo_check_reports_newer_release_with_fake_http()
    {
        var client = new GitHubUpdateClient(() => new Stub("""{"tag_name":"v9.9.9","html_url":"https://example.invalid/r"}"""));
        var release = await client.CheckBydloAsync("0.1.0");
        Assert.NotNull(release);
        Assert.Equal("v9.9.9", release.Tag);
    }

    [Fact]
    public async Task Bydlo_check_returns_null_when_up_to_date()
    {
        var client = new GitHubUpdateClient(() => new Stub("""{"tag_name":"v0.1.0","html_url":"https://example.invalid/r"}"""));
        Assert.Null(await client.CheckBydloAsync("0.1.0"));
    }

    [Fact]
    public async Task Engine_check_compares_pinned_version()
    {
        var client = new GitHubUpdateClient(() => new Stub("", url =>
            url.Contains("sing-box", StringComparison.Ordinal)
                ? """{"tag_name":"v99.0.0","html_url":"https://example.invalid/sb"}"""
                : """{"tag_name":"v1.14.3","html_url":"https://example.invalid/x"}"""));
        var newer = await client.CheckEngineAsync("https://github.com/SagerNet/sing-box", "1.14.3");
        Assert.NotNull(newer);
        var same = await client.CheckEngineAsync("https://github.com/XTLS/Xray-core", "26.10.21");
        Assert.Null(same);
    }

    [Fact]
    public async Task Non_github_upstream_is_skipped()
    {
        var client = new GitHubUpdateClient(() => new Stub("{}"));
        Assert.Null(await client.CheckEngineAsync("https://example.com/tool", "1.0"));
    }

    private sealed class Stub(string body, Func<string, string>? byUrl = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = byUrl?.Invoke(request.RequestUri?.ToString() ?? "") ?? body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
