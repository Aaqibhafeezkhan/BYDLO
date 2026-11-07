using System.Text.RegularExpressions;

namespace Bydlo.Tests;

/// <summary>
/// Issue 449: linked issues close when a pull request targets the `developer` branch, before merge.
/// The workflow runs with a write token from the default branch, so these tests pin the security
/// boundaries that keep a fork from abusing it.
/// </summary>
public sealed class CloseLinkedIssuesWorkflowTests
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

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "close-linked-issues.yml"));

    [Fact]
    public void Trigger_is_pull_request_target_for_creation_edits_and_reopen()
    {
        var workflow = ReadWorkflow();
        Assert.Contains("pull_request_target:", workflow, StringComparison.Ordinal);
        Assert.Contains("types: [opened, edited, reopened]", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  pull_request:", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Base_branch_must_be_exactly_developer()
    {
        var workflow = ReadWorkflow();
        Assert.Contains("github.event.pull_request.base.ref == 'developer'", workflow, StringComparison.Ordinal);
        // The event payload can be stale or replayed, so the live state is read again.
        Assert.Contains(".base.ref == \"developer\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("startsWith(github.event.pull_request.base.ref", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("developers", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_stay_minimal()
    {
        var workflow = ReadWorkflow();
        Assert.Contains("\npermissions: {}\n", workflow, StringComparison.Ordinal);
        Assert.Contains("contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains("issues: write", workflow, StringComparison.Ordinal);
        Assert.Contains("pull-requests: read", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("actions: write", workflow, StringComparison.Ordinal);
    }

    /// <summary>A write token must never run pull request code, so the job has no actions at all.</summary>
    [Fact]
    public void Workflow_never_checks_out_or_runs_pull_request_code()
    {
        var workflow = ReadWorkflow();
        Assert.DoesNotContain("actions/checkout", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("uses:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("git checkout", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("git clone", workflow, StringComparison.Ordinal);
    }

    /// <summary>Only structured fields may reach the shell; body, title and branch names are untrusted.</summary>
    [Fact]
    public void Pull_request_text_is_never_interpolated_into_the_script()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "github.server_url",
            "github.event.pull_request.base.ref",
            "github.event.pull_request.number",
            "github.token",
            "github.repository",
        };
        var expressions = Regex.Matches(ReadWorkflow(), @"\$\{\{([^}]*)\}\}")
            .Select(match => match.Groups[1].Value.Trim())
            .ToArray();
        Assert.NotEmpty(expressions);
        Assert.All(expressions, expression => Assert.Contains(expression, allowed));
    }

    [Fact]
    public void Parser_is_loaded_from_the_default_branch()
    {
        var workflow = ReadWorkflow();
        Assert.Contains("default_branch=$(gh api \"repos/$GH_REPO\" --jq .default_branch)", workflow, StringComparison.Ordinal);
        Assert.Contains("closing-references.sh?ref=$default_branch", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event.pull_request.base.sha", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Author_rights_are_checked_through_the_api()
    {
        var workflow = ReadWorkflow();
        Assert.Contains("collaborators/$author/permission", workflow, StringComparison.Ordinal);
        Assert.Contains("admin|maintain|write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain(".author_association", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_requests_and_closed_issues_are_skipped()
    {
        var workflow = ReadWorkflow();
        Assert.Contains(".state == \"open\" and (.pull_request | not)", workflow, StringComparison.Ordinal);
        Assert.Contains("state_reason=completed", workflow, StringComparison.Ordinal);
        // Nothing reopens an issue, so a maintainer decision survives a retarget or a close.
        Assert.DoesNotContain("state=open", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_self_test_runs_in_the_project_check()
    {
        var check = File.ReadAllText(Path.Combine(Root(), "scripts", "check.sh"));
        Assert.Contains(".github/scripts/test-closing-references.sh", check, StringComparison.Ordinal);
    }
}
