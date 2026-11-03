using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bydlo.Core.Themes;

/// <summary>Broken or unsafe custom theme file. The app falls back to the base theme.</summary>
public sealed class CustomThemeException(string message) : Exception(message)
{
}

/// <summary>Validated custom theme: base palette plus color overrides.</summary>
public sealed record CustomThemeDocument(
    string Id,
    string Name,
    string Author,
    int Version,
    bool Dark,
    IReadOnlyDictionary<string, string> Colors,
    IReadOnlyList<string> IgnoredKeys, string Description = "")
{
    /// <summary>Overrides mapped to palette resource keys (e.g. "Bg" to "BgBrush").</summary>
    public IReadOnlyDictionary<string, string> ToResourceColors() =>
        Colors.ToDictionary(kv => CustomTheme.MapKey(kv.Key), kv => CustomTheme.Normalize(kv.Value), StringComparer.Ordinal);
}

/// <summary>Theme gallery entry from <c>themes/index.json</c>.</summary>
public sealed record ThemeGalleryEntry(string Id, string Name, string Author, string Url, string Description, bool Dark, IReadOnlyDictionary<string, string> PreviewColors);

/// <summary>
/// Custom themes are JSON with colors only (issue #41). Runtime XAML is never loaded:
/// foreign AXAML could instantiate arbitrary objects and breaks trimming/AOT.
/// </summary>
public static partial class CustomTheme
{
    public const int FormatVersion = 1;

    /// <summary>Refused before parsing; an untrusted file must not force large allocations.</summary>
    public const int MaxFileBytes = 64 * 1024;

    /// <summary>Gallery index cap (entries), enforced before parsing as well.</summary>
    public const int MaxGalleryEntries = 100;

    /// <summary>
    /// Default gallery location. Canonical repository file; forks serve their own copy
    /// from the same relative path by overriding this constant.
    /// </summary>
    public const string DefaultGalleryIndexUrl = "https://raw.githubusercontent.com/endercodezz/BYDLO/main/themes/index.json";

    /// <summary>Palette keys a custom theme may override. Mirrors Purple/Light/Oled.axaml minus shadows.</summary>
    public static readonly IReadOnlyList<string> ResourceKeys =
    [
        "AccentColor", "PrimaryColor",
        "BgBrush", "SidebarBrush", "SurfaceBrush", "Surface2Brush", "SurfaceHoverBrush", "LineBrush",
        "AccentBrush", "PrimaryBrush", "AccentSoftBrush",
        "TextBrush", "TextMutedBrush", "TextFaintBrush",
        "SuccessBrush", "SuccessSoftBrush", "WarningBrush", "WarningSoftBrush",
        "DangerBrush", "DangerSoftBrush", "OnPrimaryBrush", "BackdropBrush",
        "ExpanderHeaderBackground", "ExpanderHeaderBackgroundPointerOver", "ExpanderHeaderBackgroundPressed",
        "ExpanderContentBackground", "ExpanderContentBorderBrush",
        "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderBrushPointerOver", "ExpanderHeaderBorderBrushPressed",
        "PrimaryHoverBrush", "PrimaryPressedBrush", "ButtonEdgeBrush", "NavActiveBrush", "EdgeBrush",
        "ScrimBrush",
    ];

    private static readonly HashSet<string> KnownResources = new(ResourceKeys, StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?\\z")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^#([0-9a-fA-F]{6}|[0-9a-fA-F]{8})\\z")]
    private static partial Regex HexRegex();

    /// <summary>
    /// Short theme-file key to the palette resource key. Expander tokens keep
    /// their unsuffixed names (ExpanderHeaderBackground, ...); the rest take "Brush".
    /// Unknown keys fall through and are ignored by the caller.
    /// </summary>
    public static string MapKey(string shortName)
    {
        if (shortName is "AccentColor" or "PrimaryColor" || KnownResources.Contains(shortName))
        {
            return shortName;
        }
        return shortName + "Brush";
    }

    /// <summary>Palette resource key back to the short theme-file form ("BgBrush" to "Bg").</summary>
    public static string ShortKey(string fullKey) =>
        fullKey is "AccentColor" or "PrimaryColor" ? fullKey
        : fullKey.EndsWith("Brush", StringComparison.Ordinal) ? fullKey[..^5] : fullKey;

    /// <summary>#FFRRGGBB back to the compact #RRGGBB form for exported templates.</summary>
    public static string Shorten(string value) =>
        value.Length == 9 && value.StartsWith("#FF", StringComparison.OrdinalIgnoreCase) ? "#" + value[3..] : value;

    /// <summary>Normalizes to #AARRGGBB (uppercase) or "Transparent".</summary>
    public static string Normalize(string value)
    {
        if (value.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return "Transparent";
        }
        var hex = value.ToUpperInvariant();
        return hex.Length == 7 ? "#FF" + hex[1..] : hex;
    }

    public static CustomThemeDocument Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new CustomThemeException("Theme file is empty or larger than 64 KiB.");
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CustomThemeException("Theme file is not valid JSON: " + ex.Message); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CustomThemeException("Theme root must be an object.");
            }

            var root = doc.RootElement;
            var id = RequiredString(root, "id", 1, 32);
            if (!IdRegex().IsMatch(id))
            {
                throw new CustomThemeException("Theme id must be 1-32 lowercase letters, digits or dashes.");
            }
            var name = RequiredString(root, "name", 1, 64);
            var author = OptionalString(root, "author", 64) ?? "";
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != FormatVersion)
            {
                throw new CustomThemeException("Theme version must be 1.");
            }
            if (!root.TryGetProperty("base", out var baseEl) || baseEl.ValueKind != JsonValueKind.String ||
                (baseEl.GetString() is not { } baseName) || (!baseName.Equals("dark", StringComparison.OrdinalIgnoreCase) && !baseName.Equals("light", StringComparison.OrdinalIgnoreCase)))
            {
                throw new CustomThemeException("Theme base must be \"dark\" or \"light\".");
            }
            if (!root.TryGetProperty("colors", out var colors) || colors.ValueKind != JsonValueKind.Object)
            {
                throw new CustomThemeException("Theme must define a colors object.");
            }

            var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
            var ignored = new List<string>();
            foreach (var property in colors.EnumerateObject())
            {
                if (parsed.Count + ignored.Count >= 64)
                {
                    throw new CustomThemeException("Theme defines too many colors (64 max).");
                }
                if (property.Value.ValueKind != JsonValueKind.String || !IsColor(property.Value.GetString()))
                {
                    if (!KnownResources.Contains(MapKey(property.Name)))
                    {
                        ignored.Add(property.Name);
                        continue;
                    }
                    throw new CustomThemeException($"Color {property.Name} must be #RRGGBB, #AARRGGBB or Transparent.");
                }
                if (!KnownResources.Contains(MapKey(property.Name)))
                {
                    ignored.Add(property.Name);
                    continue;
                }
                parsed[property.Name] = property.Value.GetString()!;
            }
            if (parsed.Count == 0)
            {
                throw new CustomThemeException("Theme must override at least one known color.");
            }

            return new CustomThemeDocument(id, name, author, FormatVersion,
                baseName.Equals("dark", StringComparison.OrdinalIgnoreCase), parsed, ignored, ReadDescription(root));
        }
    }

    public static IReadOnlyList<ThemeGalleryEntry> ParseIndex(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            throw new CustomThemeException("Theme gallery index is empty or larger than 64 KiB.");
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CustomThemeException("Theme gallery index is not valid JSON: " + ex.Message); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new CustomThemeException("Theme gallery index must be an array.");
            }
            var entries = new List<ThemeGalleryEntry>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (entries.Count >= MaxGalleryEntries)
                {
                    throw new CustomThemeException("Theme gallery index is too large.");
                }
                if (item.ValueKind != JsonValueKind.Object) { continue; }
                var id = ReadGalleryLabel(item, "id", 32);
                var url = ReadGalleryLabel(item, "url", 512);
                if (id is null || !IdRegex().IsMatch(id) || url is null ||
                    !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
                {
                    continue;
                }
                entries.Add(new ThemeGalleryEntry(id, ReadGalleryLabel(item, "name") ?? id, ReadGalleryLabel(item, "author") ?? "", url,
                    ReadDescription(item), !item.TryGetProperty("base", out var baseValue) ||
                    baseValue.ValueKind != JsonValueKind.String ||
                    !string.Equals(baseValue.GetString(), "light", StringComparison.OrdinalIgnoreCase),
                    ReadPreviewColors(item)));
            }
            return entries;
        }
    }

    private static string? ReadGalleryLabel(JsonElement item, string key, int max = 64) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } label && label.Length <= max ? label : null;

    private static string ReadDescription(JsonElement item)
    {
        if (!item.TryGetProperty("description", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        var description = string.Concat(value.GetString()!.Trim().EnumerateRunes()
            .Where(rune => !Rune.IsControl(rune)).Take(140).Select(rune => rune.ToString()));
        return description.Trim();
    }

    private static Dictionary<string, string> ReadPreviewColors(JsonElement item)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "Bg", "Sidebar", "Surface", "Primary", "Text" })
            {
                if (preview.TryGetProperty(key, out var color) && color.ValueKind == JsonValueKind.String &&
                    IsColor(color.GetString()))
                {
                    result[key] = Normalize(color.GetString()!);
                }
            }
        }
        return result;
    }

    private static bool IsColor(string? value) =>
        value is not null && (value.Equals("Transparent", StringComparison.OrdinalIgnoreCase) || HexRegex().IsMatch(value));

    private static string RequiredString(JsonElement root, string name, int min, int max)
    {
        var value = OptionalString(root, name, max);
        if (value is null || value.Length < min)
        {
            throw new CustomThemeException($"Theme must define \"{name}\".");
        }
        return value;
    }

    private static string? OptionalString(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } value || value.Length > max)
        {
            throw new CustomThemeException($"Theme field \"{name}\" must be a string up to {max} characters.");
        }
        return value;
    }

    /// <summary>
    /// WCAG 2.x contrast ratio. Semi-transparent text is composited over the background
    /// first, so fully transparent text scores ~1 (unreadable) instead of 21.
    /// </summary>
    public static double ContrastRatio(string text, string background)
    {
        // Flatten a translucent background over black, then the text over it.
        var bgFlat = Flatten(ParseRgba(background));
        var fgFlat = FlattenOver(bgFlat, ParseRgba(text));
        return ContrastRatio(fgFlat, bgFlat);
    }

    public static double ContrastRatio((double R, double G, double B) first, (double R, double G, double B) second)
    {
        var l1 = Luminance(first);
        var l2 = Luminance(second);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    /// <summary>WCAG AA body-text threshold.</summary>
    public static bool IsLowContrast(string text, string background) => ContrastRatio(text, background) < 4.5;

    private static double Luminance((double R, double G, double B) c)
    {
        static double Linearize(double channel) => channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        return 0.2126 * Linearize(c.R) + 0.7152 * Linearize(c.G) + 0.0722 * Linearize(c.B);
    }

    private static (double R, double G, double B) Flatten((double R, double G, double B, double A) color) =>
        FlattenOver((0, 0, 0), color);

    private static (double R, double G, double B) FlattenOver((double R, double G, double B) back, (double R, double G, double B, double A) fore) =>
        (fore.R * fore.A + back.R * (1 - fore.A),
            fore.G * fore.A + back.G * (1 - fore.A),
            fore.B * fore.A + back.B * (1 - fore.A));

    private static (double R, double G, double B, double A) ParseRgba(string hex)
    {
        if (hex.Equals("Transparent", StringComparison.OrdinalIgnoreCase))
        {
            return (1, 1, 1, 0);
        }
        var clean = Normalize(hex).TrimStart('#');
        var alpha = 1.0;
        if (clean.Length == 8)
        {
            alpha = Convert.ToInt32(clean[..2], 16) / 255.0;
            clean = clean[2..];
        }
        return (Convert.ToInt32(clean[..2], 16) / 255.0, Convert.ToInt32(clean.Substring(2, 2), 16) / 255.0, Convert.ToInt32(clean.Substring(4, 2), 16) / 255.0, alpha);
    }
}
