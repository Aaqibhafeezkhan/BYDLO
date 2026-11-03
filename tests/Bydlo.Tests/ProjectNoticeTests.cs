using System.Text.Json;
using System.Net;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class ProjectNoticeTests
{
    private const string Notice = """
        {
          "id": "v0.3-soon",
          "level": "critical",
          "title": { "en": "Fixes are coming", "ru": "Исправления скоро" },
          "text": { "en": "Don't worry, a new release is coming soon.", "ru": "Не переживайте, скоро новый релиз." },
          "url": "https://github.com/endercodezz/BYDLO/milestone/3",
          "hideFromVersion": "0.3.0",
          "expires": "2026-12-31"
        }
        """;

    [Fact]
    public void Repository_notice_file_is_empty_or_a_valid_notice()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "notice.json"));
        using var doc = JsonDocument.Parse(json);
        var published = doc.RootElement.GetProperty("id").GetString() is { Length: > 0 };
        // A published notice that fails to parse would silently show nothing to users.
        Assert.Equal(published, ProjectNoticeClient.ParseNotice(json) is not null);
    }

    [Fact]
    public void Parser_reads_localized_texts_level_link_and_limits()
    {
        var notice = ProjectNoticeClient.ParseNotice(Notice)!;
        Assert.Equal("v0.3-soon", notice.Id);
        Assert.Equal(ProjectNoticeLevel.Critical, notice.Level);
        Assert.Equal("Исправления скоро", notice.LocalizedTitle("ru"));
        Assert.Equal("Don't worry, a new release is coming soon.", notice.LocalizedText("en"));
        // Unknown languages fall back to English.
        Assert.Equal("Fixes are coming", notice.LocalizedTitle("de"));
        Assert.Equal("https://github.com/endercodezz/BYDLO/milestone/3", notice.Url);
        Assert.Equal("0.3.0", notice.HideFromVersion);
        Assert.Equal(new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero), notice.Expires);
    }

    [Fact]
    public void Plain_string_text_is_english_and_level_defaults_to_info()
    {
        var notice = ProjectNoticeClient.ParseNotice("""{"id":"a","text":"Hello"}""")!;
        Assert.Equal(ProjectNoticeLevel.Info, notice.Level);
        Assert.Equal("Hello", notice.LocalizedText("ru"));
        Assert.Equal("", notice.LocalizedTitle("ru"));
        Assert.Null(notice.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"id":"","text":{"en":"Hi"}}""")]
    [InlineData("""{"id":"a","text":{"en":"","ru":"  "}}""")]
    [InlineData("""{"id":"bad id","text":"Hi"}""")]
    [InlineData("""{"id":"a","text":"Hi","hideFromVersion":"soon"}""")]
    [InlineData("""{"id":"a","text":"Hi","expires":"tomorrow"}""")]
    public void Empty_or_malformed_notices_show_nothing(string json)
        => Assert.Null(ProjectNoticeClient.ParseNotice(json));

    [Theory]
    [InlineData("https://github.com/endercodezz/BYDLO/releases", true)]
    [InlineData("https://github.com/endercodezz/BYDLO", true)]
    [InlineData("http://github.com/endercodezz/BYDLO/releases", false)]
    [InlineData("https://github.com/endercodezz/BYDLO-fake/releases", false)]
    [InlineData("https://github.com/someone/else", false)]
    [InlineData("https://evil.example/endercodezz/BYDLO", false)]
    [InlineData("https://user@github.com/endercodezz/BYDLO", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Links_are_limited_to_the_repository(string url, bool allowed)
        => Assert.Equal(allowed, ProjectNoticeClient.SafeUrl(url) is not null);

    [Fact]
    public void Long_text_is_truncated_and_control_characters_removed()
    {
        var json = $$"""{"id":"a","text":"{{new string('x', 900)}}\u0007"}""";
        var text = ProjectNoticeClient.ParseNotice(json)!.LocalizedText("en");
        Assert.Equal(601, text.Length);
        Assert.EndsWith("…", text);
    }

    [Theory]
    [InlineData("0.2.0", "2026-11-03", true)]
    [InlineData("0.2.9", "2026-12-30", true)]
    [InlineData("0.3.0", "2026-11-03", false)]
    [InlineData("1.0.0", "2026-11-03", false)]
    [InlineData("0.2.0", "2026-12-31", false)]
    public void Notice_hides_from_the_fixed_version_and_after_expiry(string version, string date, bool active)
    {
        var notice = ProjectNoticeClient.ParseNotice(Notice)!;
        var now = DateTimeOffset.Parse(date, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(active, notice.IsActive(version, now));
    }

    [Fact]
    public async Task Checker_shows_active_notice_until_dismissed()
    {
        var requests = 0;
        var checker = Checker(() =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Notice) };
        });
        var settings = new AppSettings();
        Assert.Equal("v0.3-soon", (await checker.CheckAsync(settings, "0.2.0", false))?.Id);
        settings.DismissedNoticeId = "v0.3-soon";
        Assert.Null(await checker.CheckAsync(settings, "0.2.0", false));
        settings.DismissedNoticeId = "older";
        Assert.NotNull(await checker.CheckAsync(settings, "0.2.0", false));
        Assert.Equal(3, requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Disabled_or_blocked_startup_never_calls_http(bool enabled, bool blocked)
    {
        var checker = new ProjectNoticeChecker(new ProjectNoticeClient(() =>
            throw new Xunit.Sdk.XunitException("HTTP must not be created")));
        Assert.Null(await checker.CheckAsync(new AppSettings { CheckForUpdates = enabled }, "0.2.0", blocked));
    }

    [Fact]
    public async Task Offline_or_missing_file_shows_nothing()
    {
        Assert.Null(await Checker(() => throw new HttpRequestException("Offline"))
            .CheckAsync(new AppSettings(), "0.2.0", false));
        Assert.Null(await Checker(() => new HttpResponseMessage(HttpStatusCode.NotFound))
            .CheckAsync(new AppSettings(), "0.2.0", false));
    }

    private static ProjectNoticeChecker Checker(Func<HttpResponseMessage> send) =>
        new(new ProjectNoticeClient(() => new FakeHandler(send)),
            new FixedTime(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeHandler(Func<HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send());
    }
}
