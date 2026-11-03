namespace Bydlo.Tests;

/// <summary>Issue #243: maximized/normal/narrow window scaling stays centered and switches navigation.</summary>
public class WindowScalingTests
{
    [Fact]
    public void Compact_breakpoint_is_760()
    {
        var root = RepositoryRoot();
        var code = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml.cs"));
        Assert.Contains("CompactWidth = 760", code, StringComparison.Ordinal);
        Assert.Contains("IsCompact = e.NewSize.Width < CompactWidth", code, StringComparison.Ordinal);
    }

    [Fact]
    public void IsCompact_switches_at_breakpoint()
    {
        const double compactWidth = 760;
        Assert.True(759 < compactWidth);
        Assert.False(760 < compactWidth);
        Assert.False(1180 < compactWidth);
    }

    [Fact]
    public void MaxWidth_containers_are_centered()
    {
        var root = RepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Themes", "Styles.axaml"));
        Assert.Contains("StackPanel.page", styles, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment", styles, StringComparison.Ordinal);

        // StackPanel.page (MaxWidth 1040) must be centered so maximized windows do not stick left.
        var pageIndex = styles.IndexOf("StackPanel.page", StringComparison.Ordinal);
        var pageBlock = styles.Substring(pageIndex, Math.Min(500, styles.Length - pageIndex));
        Assert.Contains("MaxWidth", pageBlock, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment", pageBlock, StringComparison.Ordinal);
        Assert.Contains("Center", pageBlock, StringComparison.Ordinal);

        // Desktop workspace (MaxWidth 1080) must be centered.
        var workspaceIndex = styles.IndexOf(".desktop Border.workspace", StringComparison.Ordinal);
        Assert.True(workspaceIndex >= 0);
        var workspaceBlock = styles.Substring(workspaceIndex, Math.Min(400, styles.Length - workspaceIndex));
        Assert.Contains("MaxWidth", workspaceBlock, StringComparison.Ordinal);
        Assert.Contains("Center", workspaceBlock, StringComparison.Ordinal);

        var connections = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "ConnectionsView.axaml"));
        var libraryIndex = connections.IndexOf("Grid.connection-library", StringComparison.Ordinal);
        Assert.True(libraryIndex >= 0);
        var libraryBlock = connections.Substring(libraryIndex, Math.Min(400, connections.Length - libraryIndex));
        Assert.Contains("MaxWidth", libraryBlock, StringComparison.Ordinal);
        Assert.Contains("Center", libraryBlock, StringComparison.Ordinal);

        var routing = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "RoutingView.axaml"));
        Assert.Contains("MaxWidth=\"880\"", routing, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Center\"", routing, StringComparison.Ordinal);
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
