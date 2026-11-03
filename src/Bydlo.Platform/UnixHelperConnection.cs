using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

[UnsupportedOSPlatform("windows")]
public sealed partial class UnixHelperConnection : IPrivilegedOperations
{
    private readonly Socket _socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _gate = new(1);

    public async Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct = default)
    {
        NetworkSafetyGuard.EnsureAllowed("use the privileged helper");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await _socket.ConnectAsync(new UnixDomainSocketEndPoint(HelperPaths.SocketPath), timeout.Token).ConfigureAwait(false);
                if (PeerUid(_socket) != 0) { throw new UnauthorizedAccessException("The helper server is not root."); }
                _stream = new NetworkStream(_socket, ownsSocket: false);
            }
            await HelperProtocol.WriteAsync(_stream, request, ct).ConfigureAwait(false);
            return await HelperProtocol.ReadAsync<HelperReply>(_stream, ct).ConfigureAwait(false);
        }
        catch { _socket.Dispose(); throw; }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) { await _stream.DisposeAsync().ConfigureAwait(false); }
        _socket.Dispose();
        _gate.Dispose();
    }

    public static uint CurrentUid => GetUid();

    public static uint PeerUid(Socket socket)
    {
        var fd = checked((int)socket.Handle);
        if (OperatingSystem.IsMacOS())
        {
            if (GetPeerEid(fd, out var uid, out _) != 0) { throw new UnauthorizedAccessException("Cannot authenticate peer."); }
            return uid;
        }
        if (OperatingSystem.IsLinux())
        {
            var length = (uint)Marshal.SizeOf<Credentials>();
            if (GetSocketOption(fd, 1, 17, out var credentials, ref length) != 0 || length != Marshal.SizeOf<Credentials>())
            { throw new UnauthorizedAccessException("Cannot authenticate peer."); }
            return credentials.Uid;
        }
        throw new PlatformNotSupportedException();
    }

    public static void SetSocketOwner(string path, uint uid)
    {
        if (ChangeOwner(path, uid, uint.MaxValue) != 0) { throw new UnauthorizedAccessException("Cannot set socket owner."); }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public int Pid; public uint Uid; public uint Gid; }
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static partial int GetSocketOption(int socket, int level, int option, out Credentials value, ref uint length);
    [LibraryImport("libc", EntryPoint = "getpeereid", SetLastError = true)]
    private static partial int GetPeerEid(int socket, out uint uid, out uint gid);
    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();
    [LibraryImport("libc", EntryPoint = "chown", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int ChangeOwner(string path, uint uid, uint gid);
}
