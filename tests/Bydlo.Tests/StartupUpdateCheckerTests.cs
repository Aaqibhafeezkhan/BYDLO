using System.Net;
using System.Text.Json;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class StartupUpdateCheckerTests
{
    [Fact]
    public async Task Restart_retains_notice_without_another_request_and_installed_version_clears_it()
    {
        var requests = 0;
        var client = new GitHubUpdateClient(() => new FakeHandler(() =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v9.0.0","draft":false,"prerelease":false}"""),
            };
        }));
        var checker = new StartupUpdateChecker(client);
        var settings = new AppSettings();
        var release = await checker.CheckAsync(settings, "0.1.0", false);
        Assert.Equal("v9.0.0", release?.Tag);
        // Simulate settings persistence across a process restart.
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(release, await checker.CheckAsync(settings, "0.1.0", false));
        Assert.Null(await checker.CheckAsync(settings, "9.0.0", false));
        Assert.Null(settings.PendingUpdate);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_mock_startup_never_calls_http(bool enabled, bool blocked)
    {
        var checker = new StartupUpdateChecker(new GitHubUpdateClient(() =>
            throw new Xunit.Sdk.XunitException("HTTP must not be created")));
        var settings = new AppSettings { CheckForUpdates = enabled };
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", blocked));
        Assert.Null(settings.LastUpdateCheck);
    }

    [Fact]
    public async Task Offline_startup_continues_retains_notice_and_throttles_retries()
    {
        var requests = 0;
        var checker = new StartupUpdateChecker(new GitHubUpdateClient(() => new FakeHandler(() =>
        {
            requests++;
            throw new HttpRequestException("Offline");
        })));
        var settings = new AppSettings();
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", false));
        Assert.NotNull(settings.LastUpdateCheck);
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", false));
        Assert.Equal(1, requests);
        settings.PendingUpdate = new GitHubRelease("v9.0.0", "");
        settings.LastUpdateCheck = DateTimeOffset.UtcNow.AddHours(-25);
        Assert.Equal(settings.PendingUpdate, await checker.CheckAsync(settings, "0.1.0", false));
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Acknowledged_release_stays_hidden_until_next_due_check()
    {
        var settings = new AppSettings { LastUpdateCheck = DateTimeOffset.UtcNow };
        var checker = new StartupUpdateChecker(new GitHubUpdateClient(() =>
            throw new Xunit.Sdk.XunitException("HTTP must not be created")));
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", false));
    }

    [Fact]
    public async Task Check_becomes_due_at_exactly_twenty_four_hours()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var settings = new AppSettings { LastUpdateCheck = now };
        var clock = new FixedClock(now.AddHours(24).AddTicks(-1));
        var requests = 0;
        var checker = new StartupUpdateChecker(new GitHubUpdateClient(() => new FakeHandler(() =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v0.1.0"}"""),
            };
        })), clock);
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", false));
        Assert.Equal(0, requests);
        clock.Now = now.AddHours(24);
        Assert.Null(await checker.CheckAsync(settings, "0.1.0", false));
        Assert.Equal(1, requests);
        Assert.Equal(clock.Now, settings.LastUpdateCheck);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send());
    }
}
