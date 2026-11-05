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
    public async Task Raw_json_config_without_secrets_exports_ok()
    {
        var profiles = NewStore(new InMemorySecretStore());
        var raw = new Profile { Name = "raw", Protocol = ProtocolKind.SingBoxConfig };
        await profiles.AddAsync([new ImportedProfile(raw, new ProfileSecrets())]);

        var snapshot = await profiles.CaptureBackupAsync(new AppSettings());
        Assert.Single(snapshot.Profiles);
        Assert.False(BackupArchive.IsMandatory(ProtocolKind.SingBoxConfig));
        Assert.False(BackupArchive.IsMandatory(ProtocolKind.XrayConfig));
        Assert.True(BackupArchive.IsMandatory(ProtocolKind.Vless));
    }

    [Fact]
    public void Backup_validation_rejects_missing_mandatory_credentials()
    {
        var profile = new Profile { Name = "VLESS", Protocol = ProtocolKind.Vless, Host = "example.test", Port = 443 };
        var data = new BackupData { Profiles = [new ImportedProfile(profile, new ProfileSecrets())] };
        var error = Assert.Throws<InvalidDataException>(() => BackupArchive.Encrypt(data, "a long backup password"));
        Assert.Contains(profile.Id.ToString(), error.Message, StringComparison.Ordinal);
    }
}
