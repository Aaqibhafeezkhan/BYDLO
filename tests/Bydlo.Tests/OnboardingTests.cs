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
    public void Fresh_install_follows_system_theme()
    {
        Assert.Equal(ThemePreference.System, new AppSettings().Theme);
        Assert.Equal(ThemePreference.System, JsonSerializer.Deserialize<AppSettings>("{}", JsonDefaults.Options)!.Theme);
    }

    [Theory]
    [InlineData("Purple", ThemePreference.System)]
    [InlineData("Oled", ThemePreference.Oled)]
    [InlineData("Light", ThemePreference.Light)]
    [InlineData("System", ThemePreference.System)]
    public async Task Saved_theme_loads_and_legacy_purple_switches_to_system(string stored, ThemePreference expected)
    {
        // Purple is now a gallery JSON theme (issue #134); it is never installed automatically.
        var path = Path.Combine(TestDirs.New("settings"), "settings.json");
        await File.WriteAllTextAsync(path, $$"""{"Theme":"{{stored}}","CustomThemeId":null}""");
        var settings = await new SettingsStore(path).LoadAsync();
        Assert.Equal(expected, settings.Theme);
        Assert.Null(settings.CustomThemeId);
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
