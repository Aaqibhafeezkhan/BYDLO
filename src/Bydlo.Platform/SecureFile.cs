using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bydlo.Platform;

/// <summary>Writes files that contain credentials with owner-only permissions where the OS supports it.</summary>
public static class SecureFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            RestrictWindowsFile(path);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using var fs = new FileStream(path, options);
        await using var writer = new StreamWriter(fs);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindowsFile(string path)
    {
        try
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var current = WindowsIdentity.GetCurrent();
            if (current?.User is { } owner)
            {
                security.SetOwner(owner);
                security.AddAccessRule(new FileSystemAccessRule(owner,
                    FileSystemRights.FullControl, AccessControlType.Allow));
            }
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
