using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Upgrade refused by the server (status other than 101).</summary>
internal sealed class WsHandshakeException(int statusCode, string statusLine) : IOException($"HTTP {statusCode}: {statusLine}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Minimal binary WebSocket client (RFC 6455) as used by Telegram's <c>/apiws</c> endpoint: masked binary frames out,
/// fragmented messages reassembled in, ping answered, close echoed.
/// </summary>
internal sealed class RawWebSocket : IAsyncDisposable
{
    public const int MaxMessageLength = 16 * 1024 * 1024;

    private const byte OpContinuation = 0x0;
    private const byte OpText = 0x1;
    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xA;

    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly byte[] _header = new byte[14];
    private MemoryStream? _fragments;
    private int _closed;

    private RawWebSocket(Stream stream)
    {
        _stream = stream;
    }

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>
    /// Connects to <paramref name="host"/> and upgrades <paramref name="path"/> with <c>Host: domain</c>.
    /// A non-null <paramref name="sni"/> enables domain fronting (no certificate check, as upstream).
    /// </summary>
    public static async Task<RawWebSocket> ConnectAsync(ITgNetwork network, string host, string domain, string path, TimeSpan timeout,
        CancellationToken ct, string? sni = null, bool secure = true)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stream = secure
            ? await network.ConnectTlsAsync(host, 443, sni ?? domain, sni is null, cts.Token).ConfigureAwait(false)
            : await network.ConnectAsync(host, 80, cts.Token).ConfigureAwait(false);
        try
        {
            return await UpgradeAsync(stream, domain, path, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<RawWebSocket> UpgradeAsync(Stream stream, string domain, string path, CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {path} HTTP/1.1\r\nHost: {domain}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var statusLine = await ReadResponseHeadAsync(stream, ct).ConfigureAwait(false);
        var parts = statusLine.Split(' ', 3);
        var status = parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
        return status == 101 ? new RawWebSocket(stream) : throw new WsHandshakeException(status, statusLine);
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => WriteFramesAsync([data], OpBinary, ct);

    public Task SendBatchAsync(IReadOnlyList<byte[]> parts, CancellationToken ct) =>
        WriteFramesAsync([.. parts.Select(p => (ReadOnlyMemory<byte>)p)], OpBinary, ct);

    /// <summary>Next binary message, or null once the server closed the connection.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        while (!IsClosed)
        {
            var (opcode, payload, fin) = await ReadFrameAsync(ct).ConfigureAwait(false);
            switch (opcode)
            {
                case OpClose:
                    Interlocked.Exchange(ref _closed, 1);
                    try { await WriteFramesAsync([payload.AsMemory(0, Math.Min(2, payload.Length))], OpClose, ct, force: true).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    return null;
                case OpPing:
                    try { await WriteFramesAsync([payload], OpPong, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    continue;
                case OpPong:
                    continue;
                case OpContinuation or OpText or OpBinary:
                    if (fin && _fragments is null)
                    {
                        return payload;
                    }

                    _fragments ??= new MemoryStream();
                    _fragments.Write(payload);
                    if (_fragments.Length > MaxMessageLength)
                    {
                        throw new IOException($"WebSocket message too large: {_fragments.Length} bytes.");
                    }

                    if (!fin)
                    {
                        continue;
                    }

                    var message = _fragments.ToArray();
                    _fragments = null;
                    return message;
                default:
                    continue;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await WriteFramesAsync([ReadOnlyMemory<byte>.Empty], OpClose, cts.Token, force: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or NotSupportedException) { }
        }

        try { await _stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    internal static byte[] BuildFrame(byte opcode, ReadOnlySpan<byte> data, bool mask)
    {
        var length = data.Length;
        var headerLength = 2 + (length < 126 ? 0 : length < 65536 ? 2 : 8) + (mask ? 4 : 0);
        var frame = new byte[headerLength + length];
        frame[0] = (byte)(0x80 | opcode);
        var maskBit = mask ? (byte)0x80 : (byte)0;
        var at = 2;
        if (length < 126)
        {
            frame[1] = (byte)(maskBit | length);
        }
        else if (length < 65536)
        {
            frame[1] = (byte)(maskBit | 126);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)length);
            at = 4;
        }
        else
        {
            frame[1] = (byte)(maskBit | 127);
            BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2), (ulong)length);
            at = 10;
        }

        if (!mask)
        {
            data.CopyTo(frame.AsSpan(at));
            return frame;
        }

        var key = frame.AsSpan(at, 4);
        RandomNumberGenerator.Fill(key);
        var payload = frame.AsSpan(at + 4);
        data.CopyTo(payload);
        ApplyMask(payload, key);
        return frame;
    }

    private static void ApplyMask(Span<byte> payload, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] ^= key[i & 3];
        }
    }

    private static async Task<string> ReadResponseHeadAsync(Stream stream, CancellationToken ct)
    {
        // Byte by byte so nothing after the header block is consumed.
        var head = new List<byte>(256);
        var one = new byte[1];
        while (head.Count < 16 * 1024)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                break;
            }

            head.Add(one[0]);
            var n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
            {
                break;
            }
        }

        var text = Encoding.UTF8.GetString([.. head]);
        var firstLine = text.Split('\n', 2)[0].Trim();
        return firstLine.Length == 0 ? throw new WsHandshakeException(0, "empty response") : firstLine;
    }

    private async Task WriteFramesAsync(IReadOnlyList<ReadOnlyMemory<byte>> payloads, byte opcode, CancellationToken ct, bool force = false)
    {
        if (IsClosed && !force)
        {
            throw new IOException("WebSocket is closed.");
        }

        var frames = payloads.Select(p => BuildFrame(opcode, p.Span, mask: true)).ToArray();
        var buffer = frames.Length == 1 ? frames[0] : [.. frames.SelectMany(f => f)];
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<(byte Opcode, byte[] Payload, bool Fin)> ReadFrameAsync(CancellationToken ct)
    {
        await _stream.ReadExactlyAsync(_header.AsMemory(0, 2), ct).ConfigureAwait(false);
        var fin = (_header[0] & 0x80) != 0;
        var opcode = (byte)(_header[0] & 0x0F);
        var masked = (_header[1] & 0x80) != 0;
        long length = _header[1] & 0x7F;
        if (length == 126)
        {
            await _stream.ReadExactlyAsync(_header.AsMemory(2, 2), ct).ConfigureAwait(false);
            length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));
        }
        else if (length == 127)
        {
            await _stream.ReadExactlyAsync(_header.AsMemory(2, 8), ct).ConfigureAwait(false);
            length = (long)BinaryPrimitives.ReadUInt64BigEndian(_header.AsSpan(2));
        }

        if (length is < 0 or > MaxMessageLength)
        {
            throw new IOException($"WebSocket frame too large: {length} bytes.");
        }

        var key = new byte[4];
        if (masked)
        {
            await _stream.ReadExactlyAsync(key, ct).ConfigureAwait(false);
        }

        var payload = new byte[length];
        await _stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        if (masked)
        {
            ApplyMask(payload, key);
        }

        return (opcode, payload, fin);
    }
}
