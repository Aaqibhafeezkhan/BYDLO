using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

/// <summary>
/// Preview helpers for the public server catalog (issue #136): explicit per-server
/// selection before saving and latency-based ordering. Pure functions, covered by unit tests.
/// </summary>
public static class CatalogPreview
{
    /// <summary>
    /// Orders preview profiles: reachable first by ascending latency, then not yet measured
    /// (kept in their original order), then unreachable last.
    /// </summary>
    public static List<ImportedProfile> OrderByLatency(
        IReadOnlyList<ImportedProfile> profiles,
        IReadOnlyDictionary<Guid, ServerLatencyResult> latencies)
    {
        return profiles
            .Select((item, index) => (item, index))
            .OrderBy(entry => Rank(entry.item.Profile.Id, latencies))
            .ThenBy(entry => LatencyOrMax(entry.item.Profile.Id, latencies))
            .ThenBy(entry => entry.index)
            .Select(entry => entry.item)
            .ToList();
    }

    private static int Rank(Guid id, IReadOnlyDictionary<Guid, ServerLatencyResult> latencies)
    {
        if (!latencies.TryGetValue(id, out var result))
        {
            return 1;
        }

        return result.Reachable ? 0 : 2;
    }

    private static TimeSpan LatencyOrMax(Guid id, IReadOnlyDictionary<Guid, ServerLatencyResult> latencies) =>
        latencies.TryGetValue(id, out var result) && result.Reachable ? result.Latency : TimeSpan.MaxValue;

    /// <summary>Keeps the selected preview profiles in their displayed order.</summary>
    public static List<ImportedProfile> Selected(
        IReadOnlyList<ImportedProfile> profiles, Func<ImportedProfile, bool> isSelected) =>
        profiles.Where(isSelected).ToList();
}
