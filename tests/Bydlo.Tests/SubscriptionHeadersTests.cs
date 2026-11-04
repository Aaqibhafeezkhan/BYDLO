using System.Net;
using System.Text;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Issue #217: panel headers and per-subscription User-Agent, all on fake HTTP.</summary>
public sealed class SubscriptionHeadersTests
{
    private sealed class PanelHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];
        public Func<HttpResponseMessage> Respond { get; set; } = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("trojan://pw@panel.example:443#Panel"),
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            var response = Respond();
            response.Headers.TryAddWithoutValidation("subscription-userinfo", "upload=10; download=20; total=100; expire=2000000000");
            response.Headers.TryAddWithoutValidation("profile-title", "  Panel Title That Is Way Too Long And Must Be Trimmed To One Hundred Characters Exactly Here0123456789");
            response.Headers.TryAddWithoutValidation("profile-update-interval", "6");
            response.Headers.TryAddWithoutValidation("support-url", "https://panel.example/support");
            response.Headers.TryAddWithoutValidation("announce", "  Maintenance tonight  ");
            return Task.FromResult(response);
        }
    }

    private static SubscriptionClient Client(PanelHandler handler) => new(new HttpClient(handler), new ProfileImporter());

    [Fact]
    public async Task Panel_headers_land_in_metadata_and_default_ua_is_sent()
    {
        using var handler = new PanelHandler();
        var preview = await Client(handler).FetchPreviewAsync("https://panel.example/sub");
        Assert.Equal("Panel Title That Is Way Too Long And Must Be Trimmed To One Hundred Characters Exactly Here012345678",
            preview.Metadata.Title);
        Assert.Equal(100, preview.Metadata.Title!.Length);
        Assert.Equal(6, preview.Metadata.UpdateIntervalHours);
        Assert.Equal("https://panel.example/support", preview.Metadata.SupportUrl);
        Assert.Equal("Maintenance tonight", preview.Metadata.Announce);
        Assert.Equal(10, preview.Metadata.Upload);
        Assert.Equal(Bydlo.Core.AppVersion.UserAgent, Assert.Single(handler.UserAgents));
    }

    [Fact]
    public async Task Custom_user_agent_is_sent_and_bad_values_fall_back()
    {
        using var handler = new PanelHandler();
        var client = Client(handler);
        await client.FetchPreviewAsync("https://panel.example/sub", "MyPanel/1.0");
        Assert.Equal("MyPanel/1.0", handler.UserAgents[^1]);
        await client.FetchPreviewAsync("https://panel.example/sub", "  spaced  ");
        Assert.Equal("spaced", handler.UserAgents[^1]);
        await client.FetchPreviewAsync("https://panel.example/sub", "evil\r\nInjection: x");
        Assert.Equal(Bydlo.Core.AppVersion.UserAgent, handler.UserAgents[^1]);
        await client.FetchPreviewAsync("https://panel.example/sub", new string('a', 300));
        Assert.Equal(Bydlo.Core.AppVersion.UserAgent, handler.UserAgents[^1]);
        await client.FetchPreviewAsync("https://panel.example/sub", "");
        Assert.Equal(Bydlo.Core.AppVersion.UserAgent, handler.UserAgents[^1]);
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("169", null)]
    [InlineData("abc", null)]
    [InlineData("12", 12L)]
    [InlineData(" 24 ", 24L)]
    public async Task Update_interval_is_clamped(string header, long? expected)
    {
        using var handler = new PanelHandler();
        handler.Respond = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("trojan://pw@panel.example:443#P") };
            response.Headers.TryAddWithoutValidation("profile-update-interval", header);
            return response;
        };
        var preview = await Client(handler).FetchPreviewAsync("https://panel.example/sub");
        Assert.Equal(expected, preview.Metadata.UpdateIntervalHours);
    }

    [Theory]
    [InlineData("ftp://panel.example/x", null)]
    [InlineData("/relative/path", null)]
    [InlineData("https://panel.example/support", "https://panel.example/support")]
    public async Task Support_url_must_be_absolute_http(string header, string? expected)
    {
        using var handler = new PanelHandler();
        handler.Respond = () =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("trojan://pw@panel.example:443#P") };
            response.Headers.TryAddWithoutValidation("support-url", header);
            return response;
        };
        var preview = await Client(handler).FetchPreviewAsync("https://panel.example/sub");
        Assert.Equal(expected, preview.Metadata.SupportUrl);
    }

    [Fact]
    public async Task Metadata_survives_store_round_trip_and_mode_toggle_keeps_it()
    {
        var folder = TestDirs.New("sub-headers-store");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://pw@panel.example:443#Panel");
        var metadata = new SubscriptionMetadata(10, 20, 100, null, "Panel", 6, "https://panel.example/support", "Hi");
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://panel.example/sub", imported.Profiles, metadata);
        Assert.Equal("Panel", sub.Title);
        Assert.Equal(6, sub.UpdateIntervalHours);
        Assert.Equal("https://panel.example/support", sub.SupportUrl);
        Assert.Equal("Hi", sub.Announce);
        Assert.Null(sub.UserAgent);

        await store.SetSubscriptionUserAgentAsync(sub.Id, "MyPanel/1.0");
        await store.LoadAsync();
        var reloaded = Assert.Single(store.Subscriptions);
        Assert.Equal("Panel", reloaded.Title);
        Assert.Equal("MyPanel/1.0", reloaded.UserAgent);

        await store.SetSubscriptionModeAsync(sub.Id, SubscriptionMode.SelectedOnly);
        var toggled = Assert.Single(store.Subscriptions);
        Assert.Equal("Panel", toggled.Title);
        Assert.Equal(6, toggled.UpdateIntervalHours);
        Assert.Equal("MyPanel/1.0", toggled.UserAgent);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetSubscriptionUserAgentAsync(sub.Id, "a\r\nb"));
        await store.SetSubscriptionUserAgentAsync(sub.Id, "  ");
        Assert.Null(Assert.Single(store.Subscriptions).UserAgent);
    }

    [Fact]
    public async Task Auto_refresh_uses_provider_interval_and_subscription_ua()
    {
        var folder = TestDirs.New("sub-headers-schedule");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore(), new FixedClock(now));
        var imported = new ProfileImporter().ImportText("trojan://old@old.invalid:443").Profiles;
        var metadata = new SubscriptionMetadata(null, null, null, null, "Panel", 1, null, null);
        var sub = await store.AddSubscriptionWithProfilesAsync("Sub", "https://panel.example/sub", imported, metadata);
        await store.SetSubscriptionUserAgentAsync(sub.Id, "MyPanel/1.0");

        using var handler = new PanelHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), new FixedClock(now.AddHours(2)));
        // Global interval is 168 h, but the provider asked for 1 h: the 2 h-old subscription is due.
        var refreshed = await updater.RefreshIfDueAsync(new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = 168 }, false, ex => Assert.Fail(ex.ToString()));
        Assert.Equal(1, refreshed);
        Assert.Equal("MyPanel/1.0", Assert.Single(handler.UserAgents));
        Assert.Equal(6, Assert.Single(store.Subscriptions).UpdateIntervalHours);

        // The same subscription with a slow provider interval is not due after 2 h.
        var folder2 = TestDirs.New("sub-headers-schedule-slow");
        var store2 = new ProfileStore(Path.Combine(folder2, "profiles.json"), new InMemorySecretStore(), new FixedClock(now));
        var slow = await store2.AddSubscriptionWithProfilesAsync("Slow", "https://slow.example/sub",
            new ProfileImporter().ImportText("trojan://old@old.invalid:443").Profiles,
            new SubscriptionMetadata(null, null, null, null, null, 168, null, null));
        Assert.NotNull(slow.UpdateIntervalHours);
        using var handler2 = new PanelHandler();
        using var http2 = new HttpClient(handler2);
        var updater2 = new SubscriptionAutoRefresh(store2, new SubscriptionClient(http2, new ProfileImporter()), new FixedClock(now.AddHours(2)));
        Assert.Equal(0, await updater2.RefreshIfDueAsync(new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = 1 }, false, ex => Assert.Fail(ex.ToString())));
        Assert.Empty(handler2.UserAgents);
    }

    [Fact]
    public async Task Refresh_with_new_title_keeps_renamed_name()
    {
        var folder = TestDirs.New("sub-headers-rename");
        var store = new ProfileStore(Path.Combine(folder, "profiles.json"), new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://pw@panel.example:443#Panel").Profiles;
        var sub = await store.AddSubscriptionWithProfilesAsync("MyName", "https://panel.example/sub", imported,
            new SubscriptionMetadata(null, null, null, null, "OldTitle", null, null, null));
        await store.RenameSubscriptionAsync(sub.Id, "MyName");
        var fresh = new ProfileImporter().ImportText("trojan://pw2@panel2.example:443#Panel2").Profiles;
        await store.ReplaceSubscriptionProfilesAsync(sub.Id, fresh,
            new SubscriptionMetadata(null, null, null, null, "NewTitle", null, null, null));
        var updated = Assert.Single(store.Subscriptions);
        Assert.Equal("MyName", updated.Name);
        Assert.Equal("NewTitle", updated.Title);
    }

    [Fact]
    public void Report_strings_exist_in_both_languages()
    {
        foreach (var file in new[] { "en.json", "ru.json" })
        {
            var path = Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Assets", "Strings", file);
            var json = File.ReadAllText(path);
            Assert.Contains("\"wizard.sub.title\"", json, StringComparison.Ordinal);
            Assert.Contains("\"wizard.sub.support\"", json, StringComparison.Ordinal);
            Assert.Contains("\"wizard.sub.announce\"", json, StringComparison.Ordinal);
            Assert.Contains("\"subs.useragent\"", json, StringComparison.Ordinal);
            Assert.Contains("\"subs.useragent.hint\"", json, StringComparison.Ordinal);
            Assert.Contains("\"subs.useragent.save\"", json, StringComparison.Ordinal);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
