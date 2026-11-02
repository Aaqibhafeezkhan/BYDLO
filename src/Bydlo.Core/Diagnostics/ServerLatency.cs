using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Diagnostics;

/// <summary>Last latency probe of one server (issue #10).</summary>
public sealed record ServerLatencyResult(bool Reachable, TimeSpan Latency, bool Approximate);

/// <summary>
/// Measures TCP connect latency to each server (issue #10). Runs only on explicit user action:
/// the real socket path refuses to run under the development safety lock, tests inject a fake.
/// </summary>
public sealed class ServerLatencyProbe(Func<string, int, TimeSpan, CancellationToken, Task<TimeSpan?>>? connectAsync = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public const int DefaultMaxParallelism = 4;

    public async Task<IReadOnlyDictionary<Guid, ServerLatencyResult>> ProbeAsync(
        IReadOnlyList<Profile> profiles, TimeSpan? timeout = null, int maxParallelism = DefaultMaxParallelism,
        CancellationToken ct = default)
    {
        if (connectAsync is null)
        {
            NetworkSafetyGuard.EnsureAllowed("measure server latency");
        }

        var connect = connectAsync ?? RealConnectAsync;
        var limit = timeout ?? DefaultTimeout;
        var results = new ConcurrentDictionary<Guid, ServerLatencyResult>();
        await Parallel.ForEachAsync(profiles,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = ct },
            async (profile, token) =>
            {
                var latency = await MeasureAsync(connect, profile, limit, token).ConfigureAwait(false);
                results[profile.Id] = new ServerLatencyResult(latency is not null, latency ?? TimeSpan.Zero, IsApproximate(profile.Protocol));
            }).ConfigureAwait(false);
        return results;
    }

    private static async Task<TimeSpan?> MeasureAsync(
        Func<string, int, TimeSpan, CancellationToken, Task<TimeSpan?>> connect, Profile profile, TimeSpan timeout,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.Host) || profile.Port is < 1 or > 65535)
        {
            return null;
        }

        try
        {
            return await connect(profile.Host, profile.Port, timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static async Task<TimeSpan?> RealConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    /// <summary>UDP-based protocols have no TCP handshake; a TCP probe is only an approximation.</summary>
    public static bool IsApproximate(ProtocolKind protocol) => protocol is
        ProtocolKind.Hysteria2 or ProtocolKind.Tuic or ProtocolKind.WireGuard or ProtocolKind.AmneziaWG;
}

/// <summary>Pure "best server" selection over the last probe results (issue #10).</summary>
public static class BestServerPicker
{
    public static Profile? PickBest(
        IEnumerable<Profile> profiles, IReadOnlyDictionary<Guid, ServerLatencyResult> latencies)
    {
        Profile? best = null;
        var bestLatency = TimeSpan.MaxValue;
        foreach (var profile in profiles)
        {
            if (!latencies.TryGetValue(profile.Id, out var result) || !result.Reachable)
            {
                continue;
            }

            if (result.Latency < bestLatency)
            {
                best = profile;
                bestLatency = result.Latency;
            }
        }

        return best;
    }
}

/// <summary>Pure server-list ordering shared by the UI and unit tests (issue #10).</summary>
public static class ServerListOrdering
{
    public static List<Profile> Order(
        IEnumerable<Profile> profiles, IReadOnlyDictionary<Guid, ServerLatencyResult> latencies, bool sortByLatency)
    {
        if (!sortByLatency)
        {
            return profiles
                .OrderByDescending(p => p.IsFavorite)
                .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return profiles
            .OrderBy(p => latencies.TryGetValue(p.Id, out var r) && r.Reachable ? r.Latency : TimeSpan.MaxValue)
            .ThenByDescending(p => p.IsFavorite)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
