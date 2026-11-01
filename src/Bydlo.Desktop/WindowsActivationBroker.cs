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

    public void Forward(string[] args)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ExternalImportInput.LaunchInputs(args).ToArray());
        if (bytes.Length > MaxMessageBytes)
        {
            throw new IOException("Activation message is too large.");
        }

        using var client = new NamedPipeClientStream(".", _name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        client.Write(header);
        client.Write(bytes);
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_name, PipeDirection.In, 1,
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
