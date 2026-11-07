using System.Security.Cryptography;
using System.Text;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using Bydlo.Platform;

namespace Bydlo.Tests;

/// <summary>Missing secret files load as empty; unreadable paths fail closed (issue #417).</summary>
public sealed class SecretInaccessibleFileTests
{
    private sealed class PassThroughProtection : IDataProtection
    {
        public byte[] Protect(byte[] plain) => (byte[])plain.Clone();
        public byte[] Unprotect(byte[] cipher) => (byte[])cipher.Clone();
    }

    private static string SecretPath(string directory, string id)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..32];
        return Path.Combine(directory, hash + ".bin");
    }

    [Fact]
    public async Task Missing_secret_file_loads_empty_without_throwing()
    {
        var store = new DpapiSecretStore(TestDirs.New("secret-missing"), new PassThroughProtection());

        var loaded = await store.LoadAsync("no-such-id");

        Assert.Empty(loaded.Values);
    }

    [Fact]
    public async Task Missing_parent_directory_loads_empty_without_throwing()
    {
        var store = new DpapiSecretStore(
            Path.Combine(TestDirs.New("secret-missing-dir"), "no-such-subdir"),
            new PassThroughProtection());

        var loaded = await store.LoadAsync("no-such-id");

        Assert.Empty(loaded.Values);
    }

    [Fact]
    public async Task Unreadable_secret_path_throws_instead_of_returning_empty()
    {
        var dir = TestDirs.New("secret-inaccessible");
        var store = new DpapiSecretStore(dir, new PassThroughProtection());
        const string id = "http-profile";

        // A directory at the secret path reproduces the issue shape deterministically
        // on every OS without touching ACLs: File.Exists reports false for directories,
        // while reading throws an access error (UnauthorizedAccessException on Windows,
        // IOException on Unix). The old File.Exists probe returned Empty here.
        Directory.CreateDirectory(SecretPath(dir, id));

        Assert.False(File.Exists(SecretPath(dir, id)));
        var error = await Assert.ThrowsAsync<SecretAccessException>(() => store.LoadAsync(id));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
        Assert.Equal(id, error.SecretId);
        Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task Http_profile_with_unreadable_password_aborts_export()
    {
        var dir = TestDirs.New("secret-http-unreadable");
        var store = new DpapiSecretStore(dir, new PassThroughProtection());
        var profiles = new ProfileStore(Path.Combine(dir, "profiles.json"), store);
        var proxy = new Profile { Name = "http", Protocol = ProtocolKind.Http, Host = "proxy.test", Port = 8080 };
        await profiles.AddAsync([new ImportedProfile(proxy, new ProfileSecrets { Values = { [Keys.Password] = "proxy-pass" } })]);

        // Simulate the store becoming inaccessible after the password was saved.
        // File.Exists reports false for the directory, but the read fails (issue #417);
        // export must abort instead of archiving the profile without credentials.
        var bin = SecretPath(dir, proxy.Id.ToString());
        File.Delete(bin);
        Directory.CreateDirectory(bin);

        var error = await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
    }

    [Fact]
    public async Task Save_then_load_roundtrip_still_works()
    {
        var store = new DpapiSecretStore(TestDirs.New("secret-roundtrip"), new PassThroughProtection());
        await store.SaveAsync("http-profile", new ProfileSecrets { Values = { [Keys.Password] = "s3cr3t" } });

        var loaded = await store.LoadAsync("http-profile");

        Assert.Equal("s3cr3t", loaded[Keys.Password]);
    }
}
