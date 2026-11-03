using Bydlo.Core.Models;
using Bydlo.Core.Import;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class ProfileEditTests
{
    [Fact]
    public async Task Editor_saves_options_engine_and_metadata_without_mutating_the_source()
    {
        var path = Path.Combine(TestDirs.New("profile-edit"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(path, secrets);
        var original = new Profile { Name = "Original", Protocol = ProtocolKind.Vless, Host = "old.example", Port = 443,
            SubscriptionId = Guid.NewGuid(), IsFavorite = true, Options = { [Keys.Sni] = "old.example", ["custom-option"] = "retained" } };
        const string uuid = "11111111-2222-3333-4444-555555555555";
        var credentials = new ProfileSecrets { Values = { [Keys.Uuid] = uuid } };
        await store.AddAsync([new(original, credentials)]);
        var draft = new ProfileEditDraft(original, credentials)
        { Name = " Renamed ", Host = "[2001:db8::1]", Port = 8443, PreferredEngine = EngineKind.Xray };
        draft.Options[Keys.Sni] = "new.example";
        draft.Options[Keys.Transport] = "xhttp";
        draft.Options[Keys.Path] = "/new";
        var edited = draft.Build();
        await store.UpdateAsync(edited.Profile, edited.Secrets);
        draft.Options[Keys.Sni] = "unsaved.example";
        draft.Secrets.Values[Keys.Uuid] = "unsaved-uuid";

        var reopened = new ProfileStore(path, secrets);
        await reopened.LoadAsync();
        var saved = Assert.Single(reopened.Profiles);
        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.SubscriptionId, saved.SubscriptionId);
        Assert.Equal(original.AddedAt, saved.AddedAt);
        Assert.True(saved.IsFavorite);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("2001:db8::1", saved.Host);
        Assert.Equal(8443, saved.Port);
        Assert.Equal(EngineKind.Xray, saved.PreferredEngine);
        Assert.Equal("new.example", saved.Option(Keys.Sni));
        Assert.Equal("xhttp", saved.Option(Keys.Transport));
        Assert.Equal("/new", saved.Option(Keys.Path));
        Assert.Equal("retained", saved.Option("custom-option"));
        Assert.Equal("old.example", original.Option(Keys.Sni));
        Assert.Equal("Original", original.Name);
        Assert.Equal(uuid, (await secrets.LoadAsync(saved.Id.ToString()))[Keys.Uuid]);
        Assert.DoesNotContain(uuid, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("", "server.example", 443)]
    [InlineData("Test", "bad host", 443)]
    [InlineData("Test", "server.example", 0)]
    [InlineData("Test", "server.example", 65536)]
    public void Editor_rejects_invalid_name_or_endpoint(string name, string host, int port) =>
        Assert.Throws<FormatException>(() => new ProfileEditDraft(new Profile { Protocol = ProtocolKind.Vless }, ProfileSecrets.Empty)
        { Name = name, Host = host, Port = port }.Build());

    [Theory]
    [InlineData(Keys.Security, "invalid")]
    [InlineData(Keys.Transport, "invalid")]
    [InlineData(Keys.Sni, "https://invalid/path")]
    public void Editor_rejects_invalid_protocol_options(string key, string value)
    {
        var draft = new ProfileEditDraft(new Profile { Name = "Test", Protocol = ProtocolKind.Vless, Host = "server.example", Port = 443 },
            new ProfileSecrets { Values = { [Keys.Uuid] = "11111111-2222-3333-4444-555555555555" } });
        draft.Options[key] = value;
        Assert.Throws<FormatException>(() => draft.Build());
    }

    [Fact]
    public async Task Editing_raw_OpenVpn_config_updates_endpoint_and_keeps_all_sensitive_data_out_of_JSON()
    {
        var original = new ProfileImporter().ImportText("client\ndev tun\nremote old.example 1194").Profiles[0];
        var draft = new ProfileEditDraft(original.Profile, original.Secrets)
        { Name = "New VPN", Configuration = "client\ndev tun\nremote new.example 443\nproto tcp\nauth-user-pass # credentials\n<key>\nprivate-key\n</key>" };
        draft.Secrets.Values[Keys.Username] = "private-user";
        draft.Secrets.Values[Keys.Password] = "private-pass";
        var edited = draft.Build();
        var path = Path.Combine(TestDirs.New("profile-edit-raw"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(path, secrets);
        await store.AddAsync([original]);
        await store.UpdateAsync(edited.Profile, edited.Secrets);
        Assert.Equal("new.example", edited.Profile.Host);
        Assert.Equal(443, edited.Profile.Port);
        Assert.Equal("tcp", edited.Profile.Option("proto"));
        Assert.Equal("true", edited.Profile.Option("authUserPass"));
        Assert.Equal("old.example", original.Profile.Host);
        var json = await File.ReadAllTextAsync(path);
        foreach (var value in new[] { "private-key", "private-user", "private-pass" }) { Assert.DoesNotContain(value, json); }
        Assert.Contains("private-key", (await secrets.LoadAsync(original.Profile.Id.ToString()))[Keys.RawConfig]);
        draft.Configuration = "trojan://password@different.example:443";
        Assert.Throws<FormatException>(() => draft.Build());
    }

    [Fact]
    public async Task Failed_edit_restores_previous_profile_and_secrets()
    {
        var path = Path.Combine(TestDirs.New("profile-edit-failure"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(path, secrets);
        var source = new ProfileImporter().ImportText("trojan://old-password@server.example:443").Profiles[0];
        await store.AddAsync([source]);
        var draft = new ProfileEditDraft(source.Profile, source.Secrets) { Name = "Unsaved" };
        draft.Secrets.Values[Keys.Password] = "new-password";
        var edited = draft.Build();
        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.UpdateAsync(edited.Profile, edited.Secrets));
        Assert.Equal(source.Profile.Name, Assert.Single(store.Profiles).Name);
        Assert.Equal("old-password", (await secrets.LoadAsync(source.Profile.Id.ToString()))[Keys.Password]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("client\ndev tun\nremote server.example invalid")]
    [InlineData("client\ndev tun\nremote server.example 0")]
    [InlineData("client\ndev tun\nremote server.example\nport 65536")]
    public void Raw_editor_rejects_missing_config_or_invalid_ports(string? config)
    {
        var original = new ProfileImporter().ImportText("client\ndev tun\nremote server.example 1194").Profiles[0];
        Assert.Throws<FormatException>(() => new ProfileEditDraft(original.Profile, original.Secrets) { Configuration = config }.Build());
    }

    [Fact]
    public async Task OpenVpn_credentials_are_saved_separately_from_the_profile()
    {
        var path = Path.Combine(TestDirs.New("profile-edit"), "profiles.json");
        var secrets = new InMemorySecretStore();
        var store = new ProfileStore(path, secrets);
        var profile = new Profile { Protocol = ProtocolKind.OpenVpn, Name = "Original" };
        await store.AddAsync([new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.RawConfig] = "client" } })]);

        var edited = new Profile { Id = profile.Id, Protocol = profile.Protocol, Name = "Renamed" };
        var credentials = await secrets.LoadAsync(profile.Id.ToString());
        credentials.Values[Keys.Username] = "private-user";
        credentials.Values[Keys.Password] = "private-password";
        Assert.Null((await secrets.LoadAsync(profile.Id.ToString()))[Keys.Password]);
        await store.UpdateAsync(edited, credentials);

        var reopened = new ProfileStore(path, secrets);
        await reopened.LoadAsync();
        Assert.Equal("Renamed", Assert.Single(reopened.Profiles).Name);
        Assert.DoesNotContain("private-user", await File.ReadAllTextAsync(path));
        Assert.DoesNotContain("private-password", await File.ReadAllTextAsync(path));
        var saved = await secrets.LoadAsync(profile.Id.ToString());
        Assert.Equal("private-user", saved[Keys.Username]);
        Assert.Equal("private-password", saved[Keys.Password]);
        Assert.Equal("client", saved[Keys.RawConfig]);
    }
}
