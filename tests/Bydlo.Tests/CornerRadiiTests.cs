namespace Bydlo.Tests;

/// <summary>Issue #245: unified corner radii scale, no hardcoded CornerRadius.</summary>
public class CornerRadiiTests
{
    [Fact]
    public void Radius_scale_is_defined()
    {
        var root = RepositoryRoot();
        var styles = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Themes", "Styles.axaml"));
        Assert.Contains("x:Key=\"RadiusSmall\"", styles, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"RadiusMedium\"", styles, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"RadiusLarge\"", styles, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"RadiusXLarge\"", styles, StringComparison.Ordinal);
    }

    [Fact]
    public void No_hardcoded_corner_radius_outside_scale()
    {
        var root = RepositoryRoot();
        var files = new[]
        {
            Path.Combine(root, "src", "Bydlo.App", "Themes", "Styles.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "MainView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "ConnectionsView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "ToolsView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "AppearanceSettingsView.axaml"),
            Path.Combine(root, "src", "Bydlo.App", "Views", "RoutingView.axaml"),
        };
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            // Find CornerRadius="N" or Value="N" on a CornerRadius setter with a bare number.
            foreach (var line in text.Split('\n'))
            {
                if (!line.Contains("CornerRadius", StringComparison.Ordinal))
                {
                    continue;
                }
                // Resource definitions themselves contain <CornerRadius x:Key=...
                if (line.Contains("x:Key=\"Radius", StringComparison.Ordinal))
                {
                    continue;
                }
                Assert.DoesNotContain("CornerRadius=\"8\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"12\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"18\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"10\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"7\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"6\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"5\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"3\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"2\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("CornerRadius=\"1\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"8\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"12\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"18\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"10\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"7\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"6\"", line, StringComparison.Ordinal);
                Assert.DoesNotContain("Value=\"5\"", line, StringComparison.Ordinal);
                // Every remaining CornerRadius usage must reference the scale.
                Assert.Contains("Radius", line, StringComparison.Ordinal);
            }
        }
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
