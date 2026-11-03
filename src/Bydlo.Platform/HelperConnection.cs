using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Microsoft.Win32.SafeHandles;

namespace Bydlo.Platform;

/// <summary>Windows service transport. Both peers authenticate before profile secrets are transmitted.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class HelperConnection : IPrivilegedOperations
{
    public const string ServiceName = "BYDLOHelper";
    public const string PipeName = "BYDLO.Privileged.v1";
    public static string InstallDirectory => HelperPaths.InstallDirectory;
    public static string Executable => HelperPaths.Executable;
    public const PipeAccessRights ClientRights = PipeAccessRights.ReadData | PipeAccessRights.WriteData |
        PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
    private readonly NamedPipeClientStream _pipe = new(".", PipeName, ClientRights,
        PipeOptions.Asynchronous, TokenImpersonationLevel.Identification, HandleInheritability.None);
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct = default)
    {
        NetworkSafetyGuard.EnsureAllowed("use the privileged helper");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_pipe.IsConnected)
            {
                await _pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
                // A fake endpoint must never receive credentials, even if it uses the expected pipe name.
                var owner = _pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                if (!new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Equals(owner))
                { throw new UnauthorizedAccessException("The helper endpoint is not owned by SYSTEM."); }
                if (!GetNamedPipeServerProcessId(_pipe.SafePipeHandle, out var pid))
                { throw new UnauthorizedAccessException("Cannot identify helper server."); }
                using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                var imagePath = new ushort[32768];
                var length = (uint)imagePath.Length;
                if (process.IsInvalid || !QueryFullProcessImageName(process, 0, imagePath, ref length) ||
                    !string.Equals(new string(imagePath.Take(checked((int)length)).Select(value => (char)value).ToArray()), Executable, StringComparison.OrdinalIgnoreCase))
                { throw new UnauthorizedAccessException("Unexpected helper executable."); }
            }
            await HelperProtocol.WriteAsync(_pipe, request, ct).ConfigureAwait(false);
            return await HelperProtocol.ReadAsync<HelperReply>(_pipe, ct).ConfigureAwait(false);
        }
        catch { await _pipe.DisposeAsync().ConfigureAwait(false); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public static NamedPipeServerStream CreateServer(string allowedSid)
    {
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 4096, 4096, CreateSecurity(allowedSid));
    }

    public static PipeSecurity CreateSecurity(string allowedSid)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(system);
        security.AddAccessRule(new PipeAccessRule(system, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Do not use GENERIC_WRITE: its Windows mapping also grants CreateNewInstance.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(allowedSid),
            PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
            PipeAccessRights.WriteAttributes | PipeAccessRights.Synchronize | PipeAccessRights.ReadPermissions, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    public static bool Authenticate(NamedPipeServerStream pipe, string allowedSid)
    {
        string? actual = null;
        pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); actual = identity.User?.Value; });
        return string.Equals(actual, allowedSid, StringComparison.Ordinal);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] ushort[] path, ref uint size);
}
