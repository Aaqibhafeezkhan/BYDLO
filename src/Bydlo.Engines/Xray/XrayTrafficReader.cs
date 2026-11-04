using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Engines.Groups.Rpc;

namespace Bydlo.Engines.Xray;

/// <summary>Polls Xray StatsService for per-outbound deltas (issue #211).</summary>
internal sealed class XrayTrafficReader(StatsService.StatsServiceClient stats, IReadOnlyCollection<string>? outboundTags)
{
    private long _totalUp;
    private long _totalDown;

    public async Task<TrafficSample?> ReadAsync(CancellationToken ct = default)
    {
        var response = await stats.QueryStatsAsync(new QueryStatsRequest { Pattern = "outbound>>>", Reset = true },
            deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct).ConfigureAwait(false);
        if (!TrySumTraffic(response, outboundTags, out var up, out var down))
        {
            return null;
        }
        _totalUp += up;
        _totalDown += down;
        return new TrafficSample(DateTimeOffset.Now, up, down, _totalUp, _totalDown);
    }

    /// <param name="tags">Tunnel outbounds to count; null counts every outbound except direct/block.</param>
    internal static bool TrySumTraffic(QueryStatsResponse response, IReadOnlyCollection<string>? tags, out long up, out long down)
    {
        up = 0;
        down = 0;
        var found = false;
        foreach (var stat in response.Stat)
        {
            if (!stat.Name.StartsWith("outbound>>>", StringComparison.Ordinal))
            {
                continue;
            }
            var tag = TagOf(stat.Name);
            if (tags is not null ? !tags.Contains(tag) : tag is "direct" or "block")
            {
                continue;
            }
            if (stat.Name.EndsWith(">>>traffic>>>uplink", StringComparison.Ordinal))
            {
                up += stat.Value;
                found = true;
            }
            else if (stat.Name.EndsWith(">>>traffic>>>downlink", StringComparison.Ordinal))
            {
                down += stat.Value;
                found = true;
            }
        }
        return found;
    }

    private static string TagOf(string name)
    {
        var parts = name.Split(">>>", StringSplitOptions.None);
        return parts.Length == 4 ? parts[1] : "";
    }
}
