using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bydlo.Core.Import;

namespace Bydlo.Desktop;

/// <summary>Per-user, per-data-directory IPC. A second launch never initializes engines or recovery.</summary>
internal sealed class WindowsActivationBroker : IDisposable
{
    private const int MaxMessageBytes = 65536;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly string _name;
    private Task? _listener;

    public WindowsActivationBroker(string dataDirectory)
    {
        var data = Path.GetFullPath(dataDirectory).ToUpperInvariant();
        _name = "BYDLO.Import." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)));
        _mutex = new Mutex(false, _name);
        try
        {
            IsPrimary = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            IsPrimary = true;
        }
    }

    public bool IsPrimary { get; }

    public void Listen() => _listener = ListenAsync();

    public void Forward(string[] args) => ForwardAsync(args).GetAwaiter().GetResult();

    private async Task ForwardAsync(string[] args)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ExternalImportInput.LaunchInputs(args).ToArray());
        if (bytes.Length > MaxMessageBytes)
        {
            throw new IOException("Activation message is too large.");
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var client = new NamedPipeClientStream(".", _name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await client.WriteAsync(header, timeout.Token).ConfigureAwait(false);
        await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        // Keep the client alive until the server has accepted the complete request. Closing a
        // Windows pipe immediately after writing can discard data that has not been read yet.
        var acknowledgement = new byte[1];
        await client.ReadExactlyAsync(acknowledgement, timeout.Token).ConfigureAwait(false);
        if (acknowledgement[0] != 1)
        {
            throw new IOException("Activation was not acknowledged.");
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = new byte[4];
                await server.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length is <= 0 or > MaxMessageBytes)
                {
                    continue;
                }

                var bytes = new byte[length];
                await server.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                if (JsonSerializer.Deserialize<string[]>(bytes) is { } args)
                {
                    Bydlo.App.App.PendingActivations.Enqueue(ExternalImportInput.LaunchInputs(args.OfType<string>()).ToArray());
                    await server.WriteAsync(new byte[] { 1 }, timeout.Token).ConfigureAwait(false);
                    // Let the client consume the acknowledgement and close its end before
                    // disposing the server, so the acknowledgement itself cannot be lost.
                    await server.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                // A failed sender cannot tear down the running application. No arguments are logged.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.GetAwaiter().GetResult();
        _stop.Dispose();
        if (IsPrimary)
        {
            _mutex.ReleaseMutex();
        }

        _mutex.Dispose();
    }
}
