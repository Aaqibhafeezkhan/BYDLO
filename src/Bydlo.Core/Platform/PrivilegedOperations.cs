using System.Buffers.Binary;
using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Platform;

/// <summary>A session owns its engines. No executable, PID or command-line IPC operations exist.</summary>
public interface IPrivilegedOperations : IAsyncDisposable
{
    Task<HelperReply> SendAsync(HelperRequest request, CancellationToken ct = default);
}

public enum HelperOperation { Check, Start, Poll, Stop, Reconnect, ListConnections, CloseConnection }

public sealed record HelperRequest
{
    public int Version { get; init; } = HelperProtocol.Version;
    public HelperOperation Operation { get; init; }
    public EngineKind Engine { get; init; }
    public AppSettings Settings { get; init; } = new();
    public EngineStartRequest? Start { get; init; }
    public string? ConnectionId { get; init; }
}

public sealed record HelperReply
{
    public EngineAvailability? Availability { get; init; }
    public EngineState State { get; init; }
    public string? Error { get; init; }
    public string[] Logs { get; init; } = [];
    public TrafficSample? Traffic { get; init; }
    public ActiveConnection[] Connections { get; init; } = [];
}

/// <summary>Length-prefixed UTF-8 JSON, bounded before allocation; unknown versions fail closed.</summary>
public static class HelperProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxMessageBytes) { throw new InvalidDataException("Helper message too large."); }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is <= 0 or > MaxMessageBytes) { throw new InvalidDataException("Invalid helper frame size."); }
        var bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty helper message.");
    }
}
