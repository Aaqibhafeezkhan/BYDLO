namespace Bydlo.Tests;

/// <summary>Issue #311: Home must not stretch when switching to Whole device.</summary>
public class HomeLayoutTests
{
    [Fact]
    public void Home_root_scroller_disables_horizontal_growth()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Views", "HomeView.axaml"));
        Assert.Contains("<ScrollViewer HorizontalScrollBarVisibility=\"Disabled\">", view, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_tun_hint_wraps_inside_card()
    {
        var view = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Bydlo.App", "Views", "HomeView.axaml"));
        Assert.Contains("IsVisible=\"{Binding IsTun}\"", view, StringComparison.Ordinal);
        Assert.Contains("Text=\"{l:T settings.admin.hint}\" TextWrapping=\"Wrap\"", view, StringComparison.Ordinal);
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
