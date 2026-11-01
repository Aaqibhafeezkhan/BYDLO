using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Platform;
using Avalonia.Styling;
using Bydlo.Core.Models;

namespace Bydlo.App.Services;

/// <summary>Swaps the color palette at runtime and follows the OS theme when set to System.</summary>
public sealed class ThemeService
{
    private ThemePreference _preference = ThemePreference.Purple;

    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var effective = preference == ThemePreference.System ? FromSystem(app) : preference;
        var palette = new ResourceInclude(new Uri("avares://Bydlo.App/App.axaml"))
        {
            Source = new Uri($"avares://Bydlo.App/Themes/{effective switch { ThemePreference.Light => "Light", ThemePreference.Oled => "Oled", _ => "Purple" }}.axaml"),
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = palette;
        }
        else
        {
            merged.Add(palette);
        }

        app.RequestedThemeVariant = effective == ThemePreference.Light ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    /// <summary>Hooks OS theme changes (only effective while the preference is System).</summary>
    public void TrackSystemTheme()
    {
        if (Application.Current?.PlatformSettings is { } ps)
        {
            ps.ColorValuesChanged += (_, _) =>
            {
                if (_preference == ThemePreference.System)
                {
                    Apply(ThemePreference.System);
                }
            };
        }
    }

    private static ThemePreference FromSystem(Application app) =>
        app.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? ThemePreference.Light : ThemePreference.Purple;
}
