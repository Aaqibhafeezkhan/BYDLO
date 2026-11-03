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
    public void Fresh_install_follows_system_theme_and_saved_purple_is_kept()
    {
        Assert.Equal(ThemePreference.System, new AppSettings().Theme);
        Assert.Equal(ThemePreference.System, JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Options)!.Theme);
        // Users of earlier versions keep the stored choice; Purple moved to the custom group, not away (issue #134).
        var saved = JsonSerializer.Deserialize<AppSettings>("""{"Theme":"Purple"}""", JsonDefaults.Options)!;
        Assert.Equal(ThemePreference.Purple, saved.Theme);
        Assert.Contains("\"Purple\"", JsonSerializer.Serialize(saved, JsonDefaults.Options), StringComparison.Ordinal);
        var oled = JsonSerializer.Deserialize<AppSettings>("""{"Theme":"Oled"}""", JsonDefaults.Options)!;
        Assert.Equal(ThemePreference.Oled, oled.Theme);
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
