using Bydlo.Engines.SingBox;

namespace Bydlo.Tests;

/// <summary>Issue #244: traffic shows 0 bytes while VPN works.</summary>
public class TrafficCountersTests
{
    [Fact]
    public void Traffic_line_parses_up_and_down()
    {
        Assert.True(SingBoxEngine.TryParseTrafficLine("{\"up\":10,\"down\":20}", out var up, out var down));
        Assert.Equal(10, up);
        Assert.Equal(20, down);
    }

    [Fact]
    public void Traffic_line_rejects_broken_payload()
    {
        Assert.False(SingBoxEngine.TryParseTrafficLine("not json", out _, out _));
        Assert.False(SingBoxEngine.TryParseTrafficLine("{\"up\":1}", out _, out _));
        Assert.False(SingBoxEngine.TryParseTrafficLine("", out _, out _));
    }

    [Fact]
    public void SingBox_traffic_client_bypasses_proxy()
    {
        var root = RepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Engines", "SingBox", "SingBoxEngine.cs"));
        // Regression for #220: loopback /traffic must not go through the system proxy.
        Assert.Contains("UseProxy = false", code, StringComparison.Ordinal);
        Assert.Contains("TryParseTrafficLine", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_shows_honest_no_stats_message()
    {
        var root = RepositoryRoot();
        var vm = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "HomeViewModel.cs"));
        Assert.Contains("HasTraffic", vm, StringComparison.Ordinal);
        Assert.Contains("NoStatsEngine", vm, StringComparison.Ordinal);
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "HomeView.axaml"));
        Assert.Contains("stats.nostats", view, StringComparison.Ordinal);
        Assert.Contains("NoStatsEngine", view, StringComparison.Ordinal);
        Assert.Contains("HasTraffic", view, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
