using Bydlo.Core.Models;

namespace Bydlo.Tests;

public sealed class LaunchModeTests
{
    [Theory]
    [InlineData(false, null, false)]
    [InlineData(false, "0", false)]
    [InlineData(false, "1", true)]
    [InlineData(true, null, true)]
    [InlineData(true, "0", true)]
    public void MockRequiresExplicitOptIn(bool flag, string? environment, bool expected)
    {
        Assert.False(new AppSettings().MockMode);
        Assert.Equal(expected, AppSettings.IsMockForced(flag ? ["--mock"] : [], environment));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionOverridePreservesSavedPreference(bool saved)
    {
        var settings = new AppSettings { MockMode = saved, LocalProxyPort = 2345 };
        var effective = settings.WithMockMode(true);
        Assert.True(effective.MockMode);
        Assert.Equal(2345, effective.LocalProxyPort);
        Assert.Equal(saved, settings.MockMode);
        Assert.Equal(saved, settings.WithMockMode(settings.MockMode).MockMode);
    }
}
