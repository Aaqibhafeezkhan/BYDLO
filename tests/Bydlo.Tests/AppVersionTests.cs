using System.Text.RegularExpressions;
using Bydlo.Core;

namespace Bydlo.Tests;

/// <summary>
/// Guards the single-source version flow (issue #143): the assembly version stamped
/// from Directory.Build.props must match every user-visible version reference.
/// </summary>
public sealed class AppVersionTests
{
    [Fact]
    public void Current_matches_assembly_version()
    {
        var assembly = typeof(AppVersion).Assembly.GetName().Version?.ToString(3);
        Assert.Equal(assembly ?? AppVersion.Fallback, AppVersion.Current);
    }

    [Fact]
    public void UserAgent_carries_current_version()
    {
        Assert.Equal("BYDLO/" + AppVersion.Current, AppVersion.UserAgent);
        Assert.StartsWith("BYDLO/", AppVersion.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public void Current_matches_release_version_in_build_props()
    {
        Assert.Equal(ReleaseVersion(), AppVersion.Current);
    }

    [Fact]
    public void Version_references_are_synced_with_release()
    {
        var root = FindRepoRoot();
        var version = ReleaseVersion();
        var shortVersion = string.Join('.', version.Split('.').Take(2));
        Assert.Contains($"placeholder: v{version}", File.ReadAllText(Path.Combine(root, ".github", "ISSUE_TEMPLATE", "bug.yml")), StringComparison.Ordinal);
        Assert.Contains($"# Матрица совместимости (v{shortVersion})", File.ReadAllText(Path.Combine(root, "docs", "COMPATIBILITY.md")), StringComparison.Ordinal);
        Assert.Contains($"Android не входит в v{shortVersion}", File.ReadAllText(Path.Combine(root, "docs", "BUILD.md")), StringComparison.Ordinal);
        var manifest = File.ReadAllText(Path.Combine(root, "src", "Bydlo.Desktop", "app.manifest"));
        Assert.Contains("@BYDLO_MANIFEST_VERSION@", manifest, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"version=""\d+\.\d+\.\d+"), manifest);
    }

    private static string ReleaseVersion()
    {
        var props = File.ReadAllText(Path.Combine(FindRepoRoot(), "Directory.Build.props"));
        var version = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value.Trim();
        Assert.Matches(@"^\d+\.\d+\.\d+$", version);
        return version;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root with Directory.Build.props not found.");
    }
}
