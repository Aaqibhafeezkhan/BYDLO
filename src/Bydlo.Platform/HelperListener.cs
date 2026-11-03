using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

public static class HelperListener
{
    public static async IAsyncEnumerable<Stream> AcceptAsync(string owner, [EnumeratorCancellation] CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("listen for privileged requests");
        if (OperatingSystem.IsWindows())
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = HelperConnection.CreateServer(owner);
                try { await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false); }
                catch { await pipe.DisposeAsync().ConfigureAwait(false); throw; }
                if (HelperConnection.Authenticate(pipe, owner)) { yield return pipe; }
                else { await pipe.DisposeAsync().ConfigureAwait(false); }
            }
        }
        else
        {
            var uid = uint.Parse(owner, System.Globalization.CultureInfo.InvariantCulture);
            if (uid == 0) { throw new InvalidDataException("A non-root owner is required."); }
            UnixHelperSecurity.EnsureProtectedParents(HelperPaths.SocketDirectory);
            Directory.CreateDirectory(HelperPaths.SocketDirectory);
            File.SetUnixFileMode(HelperPaths.SocketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Delete(HelperPaths.SocketPath);
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(HelperPaths.SocketPath));
            UnixHelperConnection.SetSocketOwner(HelperPaths.SocketPath, uid);
            listener.Listen(4);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptAsync(ct).ConfigureAwait(false);
                    if (UnixHelperConnection.PeerUid(client) == uid) { yield return new NetworkStream(client, ownsSocket: true); }
                    else { client.Dispose(); }
                }
            }
            finally { File.Delete(HelperPaths.SocketPath); }
        }
    }
}
