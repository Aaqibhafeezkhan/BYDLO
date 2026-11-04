namespace Bydlo.Tests;

/// <summary>Issue #248: Windows code signing removes the unknown publisher warning.</summary>
public class CodeSigningTests
{
    [Fact]
    public void Signing_docs_choose_a_variant_and_link_installer()
    {
        var root = RepositoryRoot();
        var doc = File.ReadAllText(Path.Combine(root, "docs", "CODE-SIGNING.md"));
        Assert.Contains("Azure Trusted Signing", doc, StringComparison.Ordinal);
        Assert.Contains("SignPath", doc, StringComparison.Ordinal);
        Assert.Contains("BYDLO.exe", doc, StringComparison.Ordinal);
        Assert.Contains("Bydlo.Helper.exe", doc, StringComparison.Ordinal);
        Assert.Contains("#215", doc, StringComparison.Ordinal);
        Assert.Contains("#216", doc, StringComparison.Ordinal);
        Assert.Contains("timestamp", doc, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secrets", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_signs_and_verifies_windows_binaries()
    {
        var root = RepositoryRoot();
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("sign-windows", ci, StringComparison.Ordinal);
        Assert.Contains("trusted-signing-action", ci, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signtool verify /pa /v", ci, StringComparison.Ordinal);
        Assert.Contains("timestamp", ci, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secrets.AZURE_", ci, StringComparison.Ordinal);
        // No keys in the repo: only secret references.
        Assert.DoesNotContain(".pfx", ci, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PRIVATE", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void No_signing_keys_in_the_repo()
    {
        var root = RepositoryRoot();
        var banned = new[] { ".pfx", ".p12", ".snk" };
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                rel.StartsWith(".tmp" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var ext in banned)
            {
                Assert.False(file.EndsWith(ext, StringComparison.OrdinalIgnoreCase), $"Signing key must not be in repo: {rel}");
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
