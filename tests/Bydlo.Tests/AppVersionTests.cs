using Bydlo.Core;

namespace Bydlo.Tests;

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
    public void Fallback_is_synced_with_release()
    {
        Assert.Equal("0.2.0", AppVersion.Fallback);
    }
}
