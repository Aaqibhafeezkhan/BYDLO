using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Bydlo.Core.Models;
using Bydlo.Core.Themes;

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
        // Custom without loaded overrides falls back to the default dark palette; never leave a half-applied UI.
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

    /// <summary>
    /// Applies the saved preference including the custom theme file.
    /// A broken or missing custom file falls back to Purple. Returns the low-contrast flag.
    /// </summary>
    public bool ApplySaved(AppSettings settings, string dataDirectory)
    {
        if (settings.Theme == ThemePreference.Custom && settings.CustomThemeId is { } id)
        {
            var doc = CustomThemeStore.TryLoad(CustomThemeStore.ThemesDirectory(dataDirectory), id);
            if (doc is not null)
            {
                return ApplyCustom(doc);
            }
        }
        Apply(settings.Theme);
        return false;
    }

    /// <summary>
    /// Applies a custom theme over its base palette. Returns true when text contrast
    /// on the background is below WCAG AA (the theme stays applied, the UI warns).
    /// </summary>
    public bool ApplyCustom(CustomThemeDocument doc)
    {
        _preference = ThemePreference.Custom;
        var app = Application.Current;
        if (app is null)
        {
            return false;
        }

        var basePalette = new ResourceInclude(new Uri("avares://Bydlo.App/App.axaml"))
        {
            Source = new Uri($"avares://Bydlo.App/Themes/{(doc.Dark ? "Purple" : "Light")}.axaml"),
        };
        var overrides = new ResourceDictionary();
        foreach (var (key, value) in doc.ToResourceColors())
        {
            if (key is "AccentColor" or "PrimaryColor")
            {
                overrides[key] = Color.Parse(value);
            }
            else
            {
                overrides[key] = new SolidColorBrush(Color.Parse(value));
            }
        }
        var composite = new ResourceDictionary();
        composite.MergedDictionaries.Add(basePalette);
        composite.MergedDictionaries.Add(overrides);

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = composite;
        }
        else
        {
            merged.Add(composite);
        }

        app.RequestedThemeVariant = doc.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        return HasLowContrast(app);
    }

    /// <summary>Current effective palette as full resource keys to #AARRGGBB (for exporting a template).</summary>
    public static IReadOnlyDictionary<string, string> ExportCurrentPalette()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return result;
        }
        foreach (var key in CustomTheme.ResourceKeys)
        {
            if (resources.TryGetResource(key, null, out var value))
            {
                if (value is SolidColorBrush brush)
                {
                    result[key] = $"#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
                }
                else if (value is Color color)
                {
                    result[key] = $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
                }
            }
        }
        return result;
    }

    /// <summary>WCAG AA check for the applied palette. Skipped (false) when a color cannot be measured.</summary>
    public static bool HasLowContrast(Application app)
    {
        try
        {
            if (app.Resources["TextBrush"] is not SolidColorBrush text ||
                app.Resources["BgBrush"] is not SolidColorBrush bg)
            {
                return false;
            }
            var ratio = CustomTheme.ContrastRatio(ToHex(text.Color), ToHex(bg.Color));
            if (ratio >= 4.5 && app.Resources["SurfaceBrush"] is SolidColorBrush surface)
            {
                ratio = Math.Min(ratio, CustomTheme.ContrastRatio(ToHex(text.Color), ToHex(surface.Color)));
            }
            return ratio < 4.5;
        }
        catch (Exception ex) when (ex is CustomThemeException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

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
