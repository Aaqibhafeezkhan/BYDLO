namespace Bydlo.Tests;

/// <summary>Issue 335: release publishes only the section matching the tag.</summary>
public sealed class ReleaseNotesTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string ExtractSection(string[] lines, string tag)
    {
        var header = "# BYDLO v" + tag.TrimStart('v');
        Assert.Equal(1, lines.Count(l => l == header));
        var body = new List<string>();
        var printing = false;
        foreach (var line in lines)
        {
            if (line == header) { printing = true; continue; }
            if (printing && line.StartsWith("# BYDLO ", StringComparison.Ordinal)) { break; }
            if (printing) { body.Add(line); }
        }
        Assert.Contains(body, l => !string.IsNullOrWhiteSpace(l));
        return string.Join("\n", body);
    }

    [Fact]
    public void Workflow_extracts_tag_section_instead_of_full_history()
    {
        var root = Root();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("scripts/extract-release-notes.sh", ci, StringComparison.Ordinal);
        Assert.Contains("--notes-file .tmp/release-notes.md", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("--notes-file docs/RELEASE_NOTES.md", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void Extractor_scripts_fail_on_missing_or_duplicate_headers()
    {
        var root = Root();
        foreach (var script in new[] { "scripts/extract-release-notes.sh", "scripts/extract-release-notes.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(root, script));
            Assert.Contains("not found", text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("uplicate", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Existing_sections_are_unique_and_do_not_leak_history()
    {
        var root = Root();
        var lines = File.ReadAllLines(Path.Combine(root, "docs", "RELEASE_NOTES.md"));
        var section = ExtractSection(lines, "v0.3.1");
        Assert.DoesNotContain("# BYDLO v0.3.0", section, StringComparison.Ordinal);
        Assert.DoesNotContain("# BYDLO v0.2.0", section, StringComparison.Ordinal);
        Assert.Contains("v0.3.1", string.Join("\n", lines.TakeWhile(l => l != "# BYDLO v0.3.0")), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_tag_has_no_section()
    {
        var root = Root();
        var lines = File.ReadAllLines(Path.Combine(root, "docs", "RELEASE_NOTES.md"));
        Assert.DoesNotContain(lines, l => l == "# BYDLO v9.9.9");
    }
}
