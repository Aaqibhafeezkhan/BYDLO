namespace Bydlo.Core.Platform;

/// <summary>Scoped, journaled address/route/DNS changes for BYDLO's userspace tunnel.</summary>
public interface IAmneziaNetwork
{
    bool HasPendingRecovery { get; }

    Task PrepareAsync(IReadOnlyList<string> endpoints, CancellationToken ct);

    Task ApplyAsync(IReadOnlyList<string> addresses, IReadOnlyList<string> dns, IReadOnlyList<string> routes, int mtu, CancellationToken ct);

    Task RemoveAsync(CancellationToken ct = default);
}
