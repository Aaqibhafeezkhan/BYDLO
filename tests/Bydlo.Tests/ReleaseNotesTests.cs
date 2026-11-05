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
        var count = lines.Count(l => l == header);
        if (count == 0) { throw new InvalidDataException($"Release notes section not found for tag '{tag}'."); }
        if (count > 1) { throw new InvalidDataException($"Duplicate release notes headers for tag '{tag}'."); }
        var body = new List<string>();
        var printing = false;
        foreach (var line in lines)
        {
            if (line == header) { printing = true; continue; }
            if (printing && line.StartsWith("# BYDLO ", StringComparison.Ordinal)) { break; }
            if (printing) { body.Add(line); }
        }
        if (body.Count == 0 || string.IsNullOrWhiteSpace(string.Join("", body))) { throw new InvalidDataException($"Release notes section for tag '{tag}' is empty."); }
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
            Assert.Contains("is empty", text, StringComparison.OrdinalIgnoreCase);
        }
        var ps1 = File.ReadAllText(Path.Combine(root, "scripts/extract-release-notes.ps1"));
        Assert.DoesNotContain("$Input ", ps1, StringComparison.Ordinal);
        Assert.Contains("$InputPath", ps1, StringComparison.Ordinal);
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
        Assert.Throws<InvalidDataException>(() => ExtractSection(lines, "v9.9.9"));
    }

    [Fact]
    public void Duplicate_headers_are_rejected()
    {
        var lines = new[] { "# BYDLO v1.0.0", "", "notes", "", "# BYDLO v1.0.0", "", "dup" };
        Assert.Throws<InvalidDataException>(() => ExtractSection(lines, "v1.0.0"));
    }

    [Fact]
    public void Whitespace_only_section_is_rejected()
    {
        var lines = new[] { "# BYDLO v1.0.0", "   ", "", "# BYDLO v0.9.0", "", "old" };
        Assert.Throws<InvalidDataException>(() => ExtractSection(lines, "v1.0.0"));
    }

    [Fact]
    public void Fixture_section_extracts_without_leaking_history()
    {
        var lines = new[] { "# BYDLO v1.0.0", "", "new notes", "", "# BYDLO v0.9.0", "", "old" };
        var section = ExtractSection(lines, "v1.0.0");
        Assert.Contains("new notes", section, StringComparison.Ordinal);
        Assert.DoesNotContain("old", section, StringComparison.Ordinal);
    }
}
