using System.Security.Cryptography;
using Bydlo.Core.Backup;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class BackupSecretFailureTests
{
    private sealed class ThrowingProtection : IDataProtection
    {
        public byte[] Protect(byte[] plain) => (byte[])plain.Clone();
        public byte[] Unprotect(byte[] cipher) => throw new CryptographicException("Simulated decrypt failure.");
    }

    private static ProfileStore NewStore(ISecretStore secrets) =>
        new(Path.Combine(TestDirs.New("backup-secret"), "profiles.json"), secrets);

    [Fact]
    public async Task Corrupt_secret_file_aborts_export_instead_of_silently_skipping()
    {
        var dir = TestDirs.New("backup-secret-corrupt");
        var store = new DpapiSecretStore(dir, new ThrowingProtection());
        var profiles = NewStore(store);
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        await profiles.AddAsync([new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.Uuid] = "uuid-value" } })]);

        var fileError = await Assert.ThrowsAsync<SecretAccessException>(() => store.LoadAsync(profile.Id.ToString()));
        Assert.Equal("backup.secret.failed", fileError.LocalizationKey);

        var exportError = await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
        Assert.Equal("backup.secret.failed", exportError.LocalizationKey);
        Assert.Contains(profile.Id.ToString(), exportError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_mandatory_secret_aborts_export()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        await profiles.AddAsync([new ImportedProfile(profile, new ProfileSecrets())]);

        var error = await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
        Assert.Contains(profile.Id.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Equal([profile.Id.ToString()], error.LocalizationArguments);
    }

    [Fact]
    public async Task Raw_json_config_with_embedded_document_exports_ok()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var raw = new Profile { Name = "raw", Protocol = ProtocolKind.SingBoxConfig };
        await profiles.AddAsync([new ImportedProfile(raw, new ProfileSecrets { Values = { [Keys.RawConfig] = """{"outbounds":[]}""" } })]);

        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        Assert.Single(snapshot.Profiles);
        Assert.Equal([Keys.RawConfig], BackupArchive.RequiredSecretKeys(new Profile { Protocol = ProtocolKind.SingBoxConfig }));
        Assert.Equal([Keys.RawConfig], BackupArchive.RequiredSecretKeys(new Profile { Protocol = ProtocolKind.XrayConfig }));
        Assert.Equal([Keys.Uuid], BackupArchive.RequiredSecretKeys(new Profile { Protocol = ProtocolKind.Vless }));
        Assert.Empty(BackupArchive.RequiredSecretKeys(new Profile { Protocol = ProtocolKind.Http }));
        Assert.Empty(BackupArchive.RequiredSecretKeys(new Profile { Protocol = ProtocolKind.Socks5 }));
    }

    [Fact]
    public async Task Unauthenticated_http_proxy_without_secrets_exports_and_restores()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var proxy = new Profile { Name = "plain", Protocol = ProtocolKind.Http, Host = "proxy.test", Port = 8080 };
        await profiles.AddAsync([new ImportedProfile(proxy, new ProfileSecrets())]);

        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        var single = Assert.Single(snapshot.Profiles);
        Assert.Equal(ProtocolKind.Http, single.Profile.Protocol);

        // The exported backup must also validate and restore (existing backups
        // with credential-free proxies keep working, issue #344 review).
        var restored = BackupArchive.Decrypt(BackupArchive.Encrypt(snapshot, "a long backup password"), "a long backup password");
        var target = NewStore(new InMemorySecretStore());
        await target.RestoreBackupAsync(restored, BackupImportMode.Replace);
        Assert.Single(target.Profiles);
        Assert.Equal(ProtocolKind.Http, target.Profiles[0].Protocol);
    }

    [Fact]
    public async Task Vless_with_unrelated_secret_key_but_no_uuid_aborts_export()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        await profiles.AddAsync([new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.Password] = "not-a-uuid" } })]);

        // Count > 0 alone is not enough: the required UUID key is missing (issue #344 review).
        var error = await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);
    }

    [Fact]
    public async Task WireGuard_without_raw_config_aborts_export()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var profile = new Profile { Name = "WG", Protocol = ProtocolKind.WireGuard, Host = "wg.test", Port = 51820 };
        await profiles.AddAsync([new ImportedProfile(profile, new ProfileSecrets())]);

        // Raw configs carry their document inline; losing Keys.RawConfig must abort (issue #344 review).
        await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
    }

    [Fact]
    public void Backup_validation_rejects_missing_mandatory_credentials()
    {
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        var data = new BackupData { Profiles = [new ImportedProfile(profile, new ProfileSecrets())] };
        var error = Assert.Throws<InvalidDataException>(() => BackupArchive.Encrypt(data, "a long backup password"));
        Assert.Contains(profile.Id.ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShadowTls_v1_without_outer_password_exports_ok()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var v1 = new Profile { Name = "shadow-v1", Protocol = ProtocolKind.ShadowTls, Host = "sh.test", Port = 443 };
        v1.Options["version"] = "1";
        await profiles.AddAsync([new ImportedProfile(v1, new ProfileSecrets { Values = { [Keys.Password] = "inner-only" } })]);

        // The parser omits shadow_password for v1: inner password alone is valid (issue #344 review).
        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        Assert.Single(snapshot.Profiles);
    }

    [Fact]
    public async Task ShadowTls_v3_without_outer_password_aborts_export()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var v3 = new Profile { Name = "shadow-v3", Protocol = ProtocolKind.ShadowTls, Host = "sh.test", Port = 443 };
        v3.Options["version"] = "3";
        await profiles.AddAsync([new ImportedProfile(v3, new ProfileSecrets { Values = { [Keys.Password] = "inner-only" } })]);

        await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
    }

    [Fact]
    public async Task Ssh_without_username_aborts_export()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var ssh = new Profile { Name = "ssh", Protocol = ProtocolKind.Ssh, Host = "ssh.test", Port = 22 };
        await profiles.AddAsync([new ImportedProfile(ssh, new ProfileSecrets { Values = { [Keys.Password] = "pw" } })]);

        // The parser always stores the username: password alone is not a complete SSH identity (issue #344 review).
        await Assert.ThrowsAsync<SecretAccessException>(() => profiles.CaptureBackupAsync(new AppSettings()));
    }

    [Fact]
    public async Task Ssh_with_username_and_private_key_exports_ok()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var ssh = new Profile { Name = "ssh", Protocol = ProtocolKind.Ssh, Host = "ssh.test", Port = 22 };
        await profiles.AddAsync([new ImportedProfile(ssh, new ProfileSecrets { Values = { [Keys.Username] = "user", ["private_key"] = "key" } })]);

        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        Assert.Single(snapshot.Profiles);
    }

    [Fact]
    public async Task Restore_with_unreadable_existing_secret_fails_closed_without_deleting()
    {
        var dir = TestDirs.New("backup-secret-rollback-failclosed");
        var store = new DpapiSecretStore(dir, new ThrowingProtection());
        var profiles = NewStore(store);
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        await profiles.AddAsync([new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.Uuid] = "uuid-value" } })]);
        var before = Directory.GetFiles(dir);
        Assert.NotEmpty(before);

        var backup = new BackupData { Profiles = [new ImportedProfile(profile, new ProfileSecrets { Values = { [Keys.Uuid] = "new-uuid" } })] };
        var error = await Assert.ThrowsAsync<SecretAccessException>(() => profiles.RestoreBackupAsync(backup, BackupImportMode.Replace));
        Assert.Equal("backup.secret.failed", error.LocalizationKey);

        // Fail closed: nothing was written and the original ciphertext is preserved (issue #344 review).
        Assert.Equal(before, Directory.GetFiles(dir));
    }
}
