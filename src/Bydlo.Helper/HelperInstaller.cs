using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Helper;

/// <summary>Invoked only by the explicitly approved elevated installer, never by the IPC server.</summary>
[SupportedOSPlatform("windows")]
internal static class HelperInstaller
{
    public static void Install(string ownerSid)
    {
        NetworkSafetyGuard.EnsureAllowed("install the privileged helper");
        var sid = new SecurityIdentifier(ownerSid);
        if (!sid.IsAccountSid()) { throw new InvalidDataException("An account SID is required."); }
        var target = HelperConnection.InstallDirectory;
        if (Directory.Exists(target)) { throw new IOException("Remove the previous helper installation first."); }
        var source = AppContext.BaseDirectory;
        var engines = Path.GetFullPath(Path.Combine(source, "..", "engines"));
        if (!Directory.Exists(engines)) { throw new IOException("Bundled engines are missing."); }
        Directory.CreateDirectory(target);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        foreach (var account in new[] { system, admins })
        {
            acl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(target).SetAccessControl(acl);
        CopyTree(source, target);
        CopyTree(engines, Path.Combine(target, "engines"));
        File.WriteAllText(Path.Combine(target, "owner.sid"), sid.Value);
        var hashes = Directory.GetFiles(Path.Combine(target, "engines"), "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(target, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))));
        if (hashes.Count == 0) { throw new InvalidDataException("The engine bundle is empty."); }
        File.WriteAllText(Path.Combine(target, "bundle.sha256.json"), JsonSerializer.Serialize(hashes));
        var data = Path.Combine(target, "data");
        Directory.CreateDirectory(data);
        var privateAcl = new DirectorySecurity();
        privateAcl.SetAccessRuleProtection(true, false);
        foreach (var account in new[] { system, admins })
        {
            privateAcl.AddAccessRule(new FileSystemAccessRule(account, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(data).SetAccessControl(privateAcl);
        VerifyBundle();
        HelperManagement.WriteInstalledBuild(HelperManagement.GetBuild(Bydlo.Engines.Installation.EngineLock.BundleVersion));
        RunSc("create", HelperConnection.ServiceName, "binPath=", "\"" + HelperConnection.Executable + "\" service", "start=", "auto", "obj=", "LocalSystem");
        RunSc("description", HelperConnection.ServiceName, "BYDLO privileged engine helper (one explicitly authorized user)");
        RunSc("start", HelperConnection.ServiceName);
    }

    private static void CopyTree(string source, string target)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked installation paths are forbidden."); }
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { throw new InvalidDataException("Linked installation files are forbidden."); }
            var destination = Path.Combine(target, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) { CopyTree(entry, destination); }
            else { File.Copy(entry, destination, false); }
        }
    }

    public static void VerifyBundle()
    {
        var root = HelperConnection.InstallDirectory;
        var hashes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "bundle.sha256.json")))
            ?? throw new InvalidDataException("Missing bundle hashes.");
        HelperBundleIntegrity.Verify(root, hashes);
    }

    public static void Remove()
    {
        NetworkSafetyGuard.EnsureAllowed("remove the privileged helper");
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + HelperConnection.ServiceName);
        if (key is not null)
        {
            using var service = new System.ServiceProcess.ServiceController(HelperConnection.ServiceName);
            if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            { service.Stop(); service.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45)); }
            RunSc("delete", HelperConnection.ServiceName);
        }
        // Only the fixed, administrator-owned installation is deleted. This executable runs from the release folder.
        var root = Path.GetFullPath(HelperConnection.InstallDirectory);
        var expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "BYDLO Helper"));
        if (!root.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            AppContext.BaseDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        { throw new IOException("Remove the helper using the original BYDLO release folder."); }
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    private static void RunSc(params string[] args)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) { info.ArgumentList.Add(arg); }
        using var process = Process.Start(info) ?? throw new IOException("Could not start the service manager.");
        process.WaitForExit();
        if (process.ExitCode != 0) { throw new IOException("The service manager rejected the operation."); }
    }
}
