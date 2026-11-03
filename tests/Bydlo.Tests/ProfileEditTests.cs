using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class ProfileEditTests
{
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
