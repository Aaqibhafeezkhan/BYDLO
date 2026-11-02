using System.Text.Json;
using Bydlo.Core.Models;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public class OnboardingTests
{
    [Fact]
    public void Existing_settings_without_flag_do_not_show_onboarding_again()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"Theme":"Purple","Language":"ru","MockMode":false}""",
            JsonDefaults.Options)!;

        Assert.True(settings.OnboardingCompleted);
    }

    [Fact]
    public void Onboarding_flag_round_trips()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            """{"OnboardingCompleted":false}""",
            JsonDefaults.Options)!;

        Assert.False(settings.OnboardingCompleted);
    }
}
