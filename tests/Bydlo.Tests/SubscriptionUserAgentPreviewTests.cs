using System.Net;
using Bydlo.Core.Import;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Issue #350: a custom User-Agent must be usable before the first preview and saved with the subscription.</summary>
public sealed class SubscriptionUserAgentPreviewTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> UserAgents { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UserAgents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("trojan://pw@panel.example:443#Panel"),
            });
        }
    }

    [Fact]
    public async Task Preview_sends_custom_user_agent_before_any_subscription_exists()
    {
        using var handler = new RecordingHandler();
        var client = new SubscriptionClient(new HttpClient(handler), new ProfileImporter());

        var preview = await client.FetchPreviewAsync("https://panel.example/sub", "v2rayNG/1.9.0");
        Assert.False(preview.Import.IsEmpty);
        Assert.Equal("v2rayNG/1.9.0", Assert.Single(handler.UserAgents));
    }

    [Fact]
    public async Task Preview_without_user_agent_sends_default_identifier()
    {
        using var handler = new RecordingHandler();
        var client = new SubscriptionClient(new HttpClient(handler), new ProfileImporter());

        await client.FetchPreviewAsync("https://panel.example/sub");
        await client.FetchPreviewAsync("https://panel.example/sub", "  ");
        Assert.Equal(2, handler.UserAgents.Count);
        Assert.All(handler.UserAgents, ua => Assert.Equal(Bydlo.Core.AppVersion.UserAgent, ua));
    }

    [Fact]
    public async Task Subscription_saved_with_user_agent_persists_across_reload()
    {
        var file = Path.Combine(TestDirs.New("sub-ua-preview"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());
        var imported = new ProfileImporter().ImportText("trojan://pw@panel.example:443#Panel");

        var sub = await store.AddSubscriptionWithProfilesAsync(
            "Sub", "https://panel.example/sub", imported.Profiles, userAgent: "v2rayNG/1.9.0");
        Assert.Equal("v2rayNG/1.9.0", sub.UserAgent);

        await store.LoadAsync();
        Assert.Equal("v2rayNG/1.9.0", Assert.Single(store.Subscriptions).UserAgent);
    }

    [Fact]
    public async Task Blank_user_agent_is_stored_as_default_and_invalid_values_are_rejected()
    {
        var file = Path.Combine(TestDirs.New("sub-ua-blank"), "profiles.json");
        var store = new ProfileStore(file, new InMemorySecretStore());

        var blank = await store.AddSubscriptionWithProfilesAsync(
            "Blank", "https://blank.example/sub",
            new ProfileImporter().ImportText("trojan://pw@blank.example:443#B").Profiles,
            userAgent: "   ");
        Assert.Null(blank.UserAgent);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AddSubscriptionWithProfilesAsync(
            "Bad", "https://bad.example/sub",
            new ProfileImporter().ImportText("trojan://pw@bad.example:443#B").Profiles,
            userAgent: "evil\r\nInjection: x"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AddSubscriptionWithProfilesAsync(
            "Long", "https://long.example/sub",
            new ProfileImporter().ImportText("trojan://pw@long.example:443#L").Profiles,
            userAgent: new string('a', 257)));
        Assert.Single(store.Subscriptions);
    }
}
