using System.Text.RegularExpressions;

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
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "Bydlo.App"), "*.axaml", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count >= 10);
        var bareAttribute = new Regex(@"CornerRadius\s*=\s*""\d", RegexOptions.Compiled);
        var bareSetter = new Regex(@"Property\s*=\s*""CornerRadius""\s+Value\s*=\s*""\d", RegexOptions.Compiled);
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.Contains("CornerRadius", StringComparison.Ordinal))
                {
                    continue;
                }
                if (line.Contains("x:Key=\"Radius", StringComparison.Ordinal))
                {
                    continue;
                }
                Assert.False(bareAttribute.IsMatch(line), $"Hardcoded CornerRadius in {Path.GetFileName(file)}: {line.Trim()}");
                Assert.False(bareSetter.IsMatch(line), $"Hardcoded CornerRadius setter in {Path.GetFileName(file)}: {line.Trim()}");
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
