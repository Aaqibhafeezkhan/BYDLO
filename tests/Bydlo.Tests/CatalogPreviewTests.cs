using Bydlo.Core.Diagnostics;
using Bydlo.Core.Import;
using Bydlo.Core.Models;

namespace Bydlo.Tests;

/// <summary>Issue #136: explicit per-server preview selection and latency ordering on fake data.</summary>
public class CatalogPreviewTests
{
    private static List<ImportedProfile> Fixture() =>
        new ProfileImporter().ImportText(
            "trojan://fixture-password@fast.example.invalid:443#fast\n" +
            "trojan://fixture-password@slow.example.invalid:443#slow\n" +
            "trojan://fixture-password@down.example.invalid:443#down\n" +
            "trojan://fixture-password@fresh.example.invalid:443#fresh").Profiles.ToList();

    [Fact]
    public void OrderByLatency_puts_reachable_first_then_unmeasured_then_unreachable()
    {
        var profiles = Fixture();
        var byName = profiles.ToDictionary(p => p.Profile.Name);
        var latencies = new Dictionary<Guid, ServerLatencyResult>
        {
            [byName["slow"].Profile.Id] = new(true, TimeSpan.FromMilliseconds(300), false),
            [byName["fast"].Profile.Id] = new(true, TimeSpan.FromMilliseconds(20), false),
            [byName["down"].Profile.Id] = new(false, TimeSpan.Zero, false),
        };

        var ordered = CatalogPreview.OrderByLatency(profiles, latencies);

        Assert.Equal(["fast", "slow", "fresh", "down"], ordered.Select(p => p.Profile.Name));
    }

    [Fact]
    public void OrderByLatency_without_probes_keeps_original_order()
    {
        var profiles = Fixture();

        var ordered = CatalogPreview.OrderByLatency(profiles, new Dictionary<Guid, ServerLatencyResult>());

        Assert.Equal(profiles.Select(p => p.Profile.Id), ordered.Select(p => p.Profile.Id));
    }

    [Fact]
    public void Selected_keeps_displayed_order()
    {
        var profiles = Fixture();
        var selected = new HashSet<Guid> { profiles[2].Profile.Id, profiles[0].Profile.Id };

        var subset = CatalogPreview.Selected(profiles, item => selected.Contains(item.Profile.Id));

        Assert.Equal([profiles[0].Profile.Id, profiles[2].Profile.Id], subset.Select(p => p.Profile.Id));
    }
}
