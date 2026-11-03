using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Covers issue #204: corrupt and newer store files never silently wipe user data.</summary>
public sealed class JsonStoreDurabilityTests
{
    private static string ProfilesPath(string dir) => Path.Combine(dir, "profiles.json");

    private static string SettingsPath(string dir) => Path.Combine(dir, "settings.json");

    private static async Task<ProfileStore> StoreWithOneProfile(string dir)
    {
        var store = new ProfileStore(ProfilesPath(dir), new InMemorySecretStore());
        var profile = new Profile { Name = "keep", Protocol = ProtocolKind.Trojan, Host = "keep.invalid", Port = 443 };
        var secrets = new ProfileSecrets { Values = { [Keys.Password] = "pw" } };
        await store.AddAsync([new ImportedProfile(profile, secrets)]);
        return store;
    }

    [Fact]
    public async Task Truncated_profiles_are_recovered_from_backup()
    {
        var dir = TestDirs.New("store-truncated");
        await StoreWithOneProfile(dir);
        var path = ProfilesPath(dir);
        Assert.True(File.Exists(path + ".bak"));
        await File.WriteAllTextAsync(path, "{\"Version\":1,\"Profiles\":[{");
        var reopened = new ProfileStore(path, new InMemorySecretStore());
        await reopened.LoadAsync();
        Assert.Equal(StoreLoadStatus.RecoveredFromBackup, reopened.LoadStatus);
        Assert.Single(reopened.Profiles);
        var corrupt = Directory.GetFiles(dir, "profiles.json.corrupt-*");
        Assert.Single(corrupt);
    }

    [Fact]
    public async Task Corrupt_files_get_unique_names()
    {
        var dir = TestDirs.New("store-corrupt-unique");
        await StoreWithOneProfile(dir);
        var path = ProfilesPath(dir);
        File.Delete(path + ".bak");
        await File.WriteAllTextAsync(path, "not json");
        var first = new ProfileStore(path, new InMemorySecretStore());
        await first.LoadAsync();
        Assert.Equal(StoreLoadStatus.ResetEmpty, first.LoadStatus);
        await File.WriteAllTextAsync(path, "still not json");
        var second = new ProfileStore(path, new InMemorySecretStore());
        await second.LoadAsync();
        Assert.Equal(StoreLoadStatus.ResetEmpty, second.LoadStatus);
        Assert.Equal(2, Directory.GetFiles(dir, "profiles.json.corrupt-*").Length);
    }

    [Fact]
    public async Task Unknown_enum_does_not_wipe_profiles()
    {
        var dir = TestDirs.New("store-enum");
        await StoreWithOneProfile(dir);
        var path = ProfilesPath(dir);
        var json = await File.ReadAllTextAsync(path);
        json = json.Replace("\"Trojan\"", "\"BrandNewProtocol\"");
        await File.WriteAllTextAsync(path, json);
        var reopened = new ProfileStore(path, new InMemorySecretStore());
        await reopened.LoadAsync();
        Assert.NotEqual(StoreLoadStatus.Ok, reopened.LoadStatus);
        Assert.NotEmpty(Directory.GetFiles(dir, "profiles.json.corrupt-*"));
        Assert.Single(reopened.Profiles);
    }

    [Fact]
    public async Task Newer_version_is_kept_and_never_overwritten()
    {
        var dir = TestDirs.New("store-newer");
        await StoreWithOneProfile(dir);
        var path = ProfilesPath(dir);
        ProfileDocument? doc;
        await using (var fs = File.OpenRead(path))
        {
            doc = await JsonSerializer.DeserializeAsync<ProfileDocument>(fs, JsonDefaults.Options);
        }
        Assert.NotNull(doc);
        doc!.Version = 999;
        await using (var fs = File.Create(path))
        {
            await JsonSerializer.SerializeAsync(fs, doc, JsonDefaults.Options);
        }
        var reopened = new ProfileStore(path, new InMemorySecretStore());
        await reopened.LoadAsync();
        Assert.Equal(StoreLoadStatus.NewerVersion, reopened.LoadStatus);
        Assert.True(reopened.IsNewerVersion);
        Assert.Single(reopened.Profiles);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reopened.AddAsync([new ImportedProfile(new Profile { Name = "x", Protocol = ProtocolKind.Trojan, Host = "x.invalid", Port = 443 }, new ProfileSecrets())]));
        ProfileDocument? kept;
        await using (var check = File.OpenRead(path))
        {
            kept = await JsonSerializer.DeserializeAsync<ProfileDocument>(check, JsonDefaults.Options);
        }
        Assert.Equal(999, kept!.Version);
        Assert.Single(kept.Profiles);
    }

    [Fact]
    public async Task Truncated_settings_recover_from_backup()
    {
        var dir = TestDirs.New("store-settings");
        var path = SettingsPath(dir);
        var store = new SettingsStore(path);
        await store.SaveAsync(new AppSettings { LocalProxyPort = 2480 });
        Assert.True(File.Exists(path + ".bak"));
        await File.WriteAllTextAsync(path, "{\"LocalProxyPort\":");
        var reopened = new SettingsStore(path);
        var settings = await reopened.LoadAsync();
        Assert.Equal(StoreLoadStatus.RecoveredFromBackup, reopened.LoadStatus);
        Assert.Equal(2480, settings.LocalProxyPort);
    }

    [Fact]
    public async Task Atomic_write_survives_deserialization()
    {
        var dir = TestDirs.New("store-atomic");
        var path = SettingsPath(dir);
        var store = new SettingsStore(path);
        var settings = new AppSettings { LocalProxyPort = 2599 };
        await store.SaveAsync(settings);
        Assert.False(File.Exists(path + ".tmp"));
        var reread = await new SettingsStore(path).LoadAsync();
        Assert.Equal(2599, reread.LocalProxyPort);
    }
}
