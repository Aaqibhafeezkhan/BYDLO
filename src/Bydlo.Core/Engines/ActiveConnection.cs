namespace Bydlo.Core.Engines;

public sealed record ActiveConnection(string Id, string Host, string Process, string Rule, string Outbound,
    long DownloadBytes, long UploadBytes);

public interface IActiveConnectionSource
{
    Task<IReadOnlyList<ActiveConnection>> ListConnectionsAsync(CancellationToken ct = default);
    Task CloseConnectionAsync(string id, CancellationToken ct = default);
}
