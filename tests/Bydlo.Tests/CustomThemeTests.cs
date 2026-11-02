using Bydlo.Core.Themes;

namespace Bydlo.Tests;

/// <summary>Issue #41: custom JSON themes — parser, validation, contrast, store.</summary>
public class CustomThemeTests
{
    private const string Valid = """
        {
          "id": "ocean",
          "name": "Ocean",
          "author": "someone",
          "version": 1,
          "base": "dark",
          "colors": {
            "Bg": "#0F1A24",
            "Text": "#E8F1F5",
            "Primary": "#2E8FB8",
            "AccentColor": "#5EC8E5",
            "Mystery": "#123456"
          }
        }
        """;

    [Fact]
    public void Parse_accepts_valid_theme_and_ignores_unknown_keys()
    {
        var doc = CustomTheme.Parse(Valid);
        Assert.Equal("ocean", doc.Id);
        Assert.True(doc.Dark);
        Assert.Equal(["Mystery"], doc.IgnoredKeys);
        var resources = doc.ToResourceColors();
        Assert.Equal("#FF0F1A24", resources["BgBrush"]);
        Assert.Equal("#FF5EC8E5", resources["AccentColor"]);
    }

    [Theory]
    [InlineData("""{"id": "Bad","name": "N","version": 1,"base": "dark","colors": {"Bg": "#000000"}}""")]
    [InlineData("""{"id": "","name": "N","version": 1,"base": "dark","colors": {"Bg": "#000000"}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 2,"base": "dark","colors": {"Bg": "#000000"}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 1,"base": "neon","colors": {"Bg": "#000000"}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 1,"base": "dark","colors": {"Bg": "red"}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 1,"base": "dark","colors": {"Bg": "#FFF"}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 1,"base": "dark","colors": {}}""")]
    [InlineData("""{"id": "ok","name": "N","version": 1,"base": "dark"}""")]
    [InlineData("""{"id": "ok","version": 1,"base": "dark","colors": {"Bg": "#000000"}}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{\"id\": \"ok\\n\",\"name\": \"N\",\"version\": 1,\"base\": \"dark\",\"colors\": {\"Bg\": \"#000000\"}}")]
    [InlineData("{\"id\": \"ok\",\"name\": \"N\",\"version\": 1,\"base\": \"dark\",\"colors\": {\"Bg\": \"#000000\\n\"}}")]
    [InlineData("{\"id\": \"ok\",\"name\": \"N\",\"version\": 1,\"base\": \"dark\",\"colors\": {\"Bg\": \" #000000\"}}")]
    [InlineData("{\"id\": \"ok\",\"name\": \"N\",\"version\": 1,\"base\": \"dark\",\"colors\": {\"Bg\": \"#000000 \"}}")]
    public void Parse_rejects_broken_themes(string json) => Assert.Throws<CustomThemeException>(() => CustomTheme.Parse(json));

    [Fact]
    public void Parse_rejects_oversized_files()
    {
        var big = "{\"id\": \"ok\",\"name\": \"N\",\"version\": 1,\"base\": \"dark\",\"colors\": {\"Bg\": \"#000000\", \"Pad\": \""
            + new string('x', CustomTheme.MaxFileBytes) + "\"}}";
        Assert.Throws<CustomThemeException>(() => CustomTheme.Parse(big));
    }

    [Fact]
    public void Keys_map_both_ways()
    {
        Assert.Equal("BgBrush", CustomTheme.MapKey("Bg"));
        Assert.Equal("AccentColor", CustomTheme.MapKey("AccentColor"));
        Assert.Equal("Bg", CustomTheme.ShortKey("BgBrush"));
        Assert.Equal("PrimaryColor", CustomTheme.ShortKey("PrimaryColor"));
        Assert.Equal("#FF112233", CustomTheme.Normalize("#112233"));
        Assert.Equal("#112233", CustomTheme.Shorten("#FF112233"));
        Assert.Equal("#80112233", CustomTheme.Shorten("#80112233"));
    }

    [Fact]
    public void Expander_keys_keep_their_unsuffixed_names()
    {
        Assert.Equal("ExpanderHeaderBackground", CustomTheme.MapKey("ExpanderHeaderBackground"));
        Assert.Equal("ExpanderHeaderBackgroundPointerOver", CustomTheme.MapKey("ExpanderHeaderBackgroundPointerOver"));
        Assert.Equal("ExpanderContentBackground", CustomTheme.MapKey("ExpanderContentBackground"));
        Assert.Equal("ExpanderHeaderBorderBrush", CustomTheme.MapKey("ExpanderHeaderBorderBrush"));
        var doc = CustomTheme.Parse("""
            {"id": "exp", "name": "Exp", "version": 1, "base": "dark",
             "colors": {"ExpanderHeaderBackground": "#112233", "ExpanderContentBorderBrush": "#445566"}}
            """);
        Assert.Empty(doc.IgnoredKeys);
        Assert.Equal("#FF112233", doc.ToResourceColors()["ExpanderHeaderBackground"]);
        Assert.Equal("#FF445566", doc.ToResourceColors()["ExpanderContentBorderBrush"]);
    }

    [Fact]
    public void Contrast_matches_wcag_reference_points()
    {
        Assert.Equal(21, CustomTheme.ContrastRatio("#FFFFFF", "#000000"), 3);
        Assert.Equal(1, CustomTheme.ContrastRatio("#123456", "#123456"), 3);
        Assert.False(CustomTheme.IsLowContrast("#FFFFFF", "#000000"));
        Assert.True(CustomTheme.IsLowContrast("#000000", "#000000"));
        Assert.True(CustomTheme.IsLowContrast("#555555", "#141418"));
        Assert.False(CustomTheme.IsLowContrast("#EFEDF3", "#141418"));
    }

    [Fact]
    public void Contrast_composites_transparency_over_the_background()
    {
        Assert.True(CustomTheme.IsLowContrast("#00FFFFFF", "#000000"));
        Assert.True(CustomTheme.IsLowContrast("Transparent", "#000000"));
        Assert.False(CustomTheme.IsLowContrast("#80FFFFFF", "#000000"));
        Assert.False(CustomTheme.IsLowContrast("#FFFFFF", "#000000"));
    }

    [Fact]
    public void Store_round_trips_and_skips_broken_files()
    {
        var dir = TestDirs.New("themes");
        var doc = CustomThemeStore.Save(dir, Valid);
        Assert.Equal("ocean", doc.Id);
        Assert.Equal(doc.Name, CustomThemeStore.TryLoad(dir, "ocean")?.Name);
        Assert.Null(CustomThemeStore.TryLoad(dir, "missing"));
        File.WriteAllText(Path.Combine(dir, "broken.json"), "nope");
        Assert.Single(CustomThemeStore.ListAll(dir));
        Assert.True(CustomThemeStore.Delete(dir, "ocean"));
        Assert.False(CustomThemeStore.Delete(dir, "ocean"));
        Assert.Empty(CustomThemeStore.ListAll(dir));
    }

    [Fact]
    public void Gallery_index_parses_and_skips_bad_entries()
    {
        var entries = CustomTheme.ParseIndex("""
            [
              {"id": "ocean", "name": "Ocean", "author": "A", "url": "https://example.invalid/ocean.json"},
              {"id": "bad!", "name": "Bad", "url": "https://example.invalid/bad.json"},
              {"id": "plain", "name": "Plain", "url": "http://example.invalid/plain.json"}
            ]
            """);
        var entry = Assert.Single(entries);
        Assert.Equal("ocean", entry.Id);
        Assert.Throws<CustomThemeException>(() => CustomTheme.ParseIndex("[]{}"));
    }
}
