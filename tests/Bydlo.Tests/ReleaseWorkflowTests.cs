namespace Bydlo.Tests;

/// <summary>Issue 336: draft release must run after a successful package even with an allowed sign skip.</summary>
public sealed class ReleaseWorkflowTests
{
    private static string ReadCi()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, ".github", "workflows", "ci.yml"));
    }

    private static string ReleaseBlock(string ci)
    {
        var start = ci.IndexOf("\n  release:", StringComparison.Ordinal);
        Assert.True(start >= 0);
        return ci.Substring(start, Math.Min(1200, ci.Length - start));
    }

    [Fact]
    public void Draft_release_checks_package_result_explicitly()
    {
        var block = ReleaseBlock(ReadCi());
        Assert.Contains("needs.package.result == 'success'", block, StringComparison.Ordinal);
        Assert.Contains("!cancelled()", block, StringComparison.Ordinal);
        Assert.Contains("startsWith(github.ref, 'refs/tags/v')", block, StringComparison.Ordinal);
        Assert.Contains("github.server_url == 'https://github.com'", block, StringComparison.Ordinal);
        Assert.Contains("needs: package", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Package_still_gates_on_build_and_signing()
    {
        var ci = ReadCi();
        Assert.Contains("needs.desktop.result == 'success'", ci, StringComparison.Ordinal);
        Assert.Contains("needs.windows-parity.result == 'success'", ci, StringComparison.Ordinal);
        Assert.Contains("needs.macos.result == 'success'", ci, StringComparison.Ordinal);
        Assert.Contains("needs.sign-windows.result == 'success'", ci, StringComparison.Ordinal);
    }
}
