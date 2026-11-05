using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Bydlo.App.Themes;

// Built-in palettes as compiled XAML classes. Creating them directly keeps theme switching working in trimmed builds,
// where looking up precompiled XAML by avares:// URI fails. Colors stay in the .axaml files.
public sealed partial class LightPalette : ResourceDictionary
{
    public LightPalette() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class OledPalette : ResourceDictionary
{
    public OledPalette() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class PurplePalette : ResourceDictionary
{
    public PurplePalette() => AvaloniaXamlLoader.Load(this);
}
