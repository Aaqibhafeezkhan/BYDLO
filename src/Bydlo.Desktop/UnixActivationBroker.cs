using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Bydlo.Core.Import;

namespace Bydlo.Desktop;

/// <summary>Owner-only local IPC; secondary launches never initialize application services.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixActivationBroker : IDisposable
{
    internal const int MaxMessageBytes = 65536;
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly FileStream? _lock;
    private readonly Action<string[]> _activate;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _socketPath;
    private readonly TimeSpan _timeout;
    private Socket? _server;
    private Task? _listener;
    private bool _disposed;

    public UnixActivationBroker(string dataDirectory, Action<string[]> activate, TimeSpan? timeout = null)
    {
        _activate = activate;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        var directory = Path.Combine(Path.GetFullPath(dataDirectory), "ipc");
        _socketPath = Path.Combine(directory, "desktop.sock");
        // The directory, rather than socket mode bits alone, is the access boundary on macOS.
        Directory.CreateDirectory(directory, DirectoryMode);
        if (new DirectoryInfo(directory).LinkTarget is not null || File.GetUnixFileMode(directory) != DirectoryMode)
        {
            throw new IOException("The activation directory must be a private directory with mode 0700.");
        }

        try
        {
            // Preserve the inode, including after shutdown or a crash. Only the lock owner may
            // replace a stale socket. Keep the existing desktop.lock for upgrade compatibility.
            _lock = new FileStream(Path.Combine(dataDirectory, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // A lock failure is never permission to initialize engines or recovery.
        }
    }

    public bool IsPrimary => _lock is not null;

    public void Listen()
    {
        if (!IsPrimary || _server is not null) { throw new InvalidOperationException(); }
        File.Delete(_socketPath);
        _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _server.Bind(new UnixDomainSocketEndPoint(_socketPath));
        File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _server.Listen(8);
        _listener = ListenAsync();
    }

    public void Forward(string[] args) => ForwardAsync(args).GetAwaiter().GetResult();

    internal async Task ForwardAsync(string[] args)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ExternalImportInput.LaunchInputs(args).ToArray(), DesktopJsonContext.Default.StringArray);
        if (bytes.Length > MaxMessageBytes) { throw new IOException("Activation message is too large."); }
        using var timeout = new CancellationTokenSource(_timeout);
        // The primary may have claimed its lock before binding the socket. Retry only connection
        // establishment; never resend a request whose acknowledgement may have been lost.
        Socket client;
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await client.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), timeout.Token).ConfigureAwait(false);
                break;
            }
            catch (SocketException)
            {
                client.Dispose();
                await Task.Delay(50, timeout.Token).ConfigureAwait(false);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        await using var stream = new NetworkStream(client, ownsSocket: true);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        var acknowledgement = new byte[1];
        await stream.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
        if (acknowledgement[0] != 1) { throw new IOException("Activation was not acknowledged."); }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _server!.AcceptAsync(_stop.Token).ConfigureAwait(false);
                await using var stream = new NetworkStream(client, ownsSocket: false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(_timeout);
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxMessageBytes) { continue; }
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                if (JsonSerializer.Deserialize(bytes, DesktopJsonContext.Default.StringArray) is { } args)
                {
                    _activate(ExternalImportInput.LaunchInputs(args.OfType<string>()).ToArray());
                    await stream.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException)
            {
                // Reject malformed, disconnected or stalled clients without logging their inputs.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _stop.Cancel();
        try
        {
            _listener?.GetAwaiter().GetResult();
            _server?.Dispose();
            if (IsPrimary && _server is not null)
            {
                try { File.Delete(_socketPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The next lock owner can remove a stale endpoint; always release the lock.
                }
            }
        }
        finally
        {
            _lock?.Dispose();
            _stop.Dispose();
        }
    }
}
