using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class SecureFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows_write_removes_broad_access_without_changing_owner(bool existing)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var directory = TestDirs.New("secure-file");
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var directoryInfo = new DirectoryInfo(directory);
        var directoryAcl = directoryInfo.GetAccessControl();
        directoryAcl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read,
            InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directoryInfo.SetAccessControl(directoryAcl);
        var path = Path.Combine(directory, "credentials.txt");
        SecurityIdentifier? owner = null;
        if (existing)
        {
            await File.WriteAllTextAsync(path, "old content that must be truncated");
            var file = new FileInfo(path);
            var acl = file.GetAccessControl();
            owner = (SecurityIdentifier)acl.GetOwner(typeof(SecurityIdentifier))!;
            // Deterministically reproduce a token that may change the DACL but not the owner.
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.TakeOwnership, AccessControlType.Deny));
            acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(acl);
        }

        try
        {
            await SecureFile.WriteAsync(path, "secret", CancellationToken.None);

            Assert.Equal("secret", await File.ReadAllTextAsync(path));
            AssertRestrictedAcl(path, user);
            if (owner is not null)
                Assert.Equal(owner, new FileInfo(path).GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Windows_acl_failure_leaves_no_new_secrets_on_disk()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var identity = WindowsIdentity.GetCurrent();
        var directory = TestDirs.New("secure-file-denied");
        var path = Path.Combine(directory, "credentials.txt");
        await File.WriteAllTextAsync(path, "old content");
        var file = new FileInfo(path);
        var original = file.GetAccessControl();
        // Retain a handle with WRITE_DAC so cleanup does not need elevation after the deny.
        using var restore = file.Create(FileMode.Open,
            FileSystemRights.ChangePermissions, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.None, null);
        var denied = file.GetAccessControl();
        // OWNER RIGHTS overrides the owner's implicit WRITE_DAC permission.
        denied.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-3-4"),
            FileSystemRights.ChangePermissions, AccessControlType.Deny));
        // Also deny the user directly: an elevated runner can otherwise obtain
        // WRITE_DAC through Administrators even after removing implicit owner rights.
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!,
            FileSystemRights.ChangePermissions, AccessControlType.Deny));
        file.SetAccessControl(denied);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                SecureFile.WriteAsync(path, "must never be written", CancellationToken.None));
            Assert.Empty(await File.ReadAllTextAsync(path));
        }
        finally
        {
            restore.SetAccessControl(original);
            restore.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertRestrictedAcl(string path, SecurityIdentifier user)
    {
        var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected);
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToArray();
        var expected = new[]
        {
            user,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        };
        Assert.Equal(expected.Length, rules.Length);
        foreach (var sid in expected)
        {
            var rule = Assert.Single(rules, rule => rule.IdentityReference.Equals(sid));
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        }
    }
}
