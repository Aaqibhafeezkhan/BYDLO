using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace Bydlo.App.Services;

/// <summary>
/// Runtime-switchable localization. Strings live in <c>Assets/Strings/{lang}.json</c>; missing keys fall back to English.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static readonly string[] Supported = ["en", "ru"];

    private Dictionary<string, string> _strings = [];
    private Dictionary<string, string> _fallback = [];

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Language { get; private set; } = "en";

    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v : _fallback.TryGetValue(key, out var f) ? f : key;

    /// <summary>Formats a string with arguments: <c>T("home.reconnect", 2)</c>.</summary>
    public static string T(string key, params object[] args) =>
        args.Length == 0 ? Instance[key] : string.Format(CultureInfo.CurrentCulture, Instance[key], args);

    /// <summary>"auto" picks Russian for ru/uk/be/kk system cultures, English otherwise.</summary>
    public static string Resolve(string preference)
    {
        if (Supported.Contains(preference))
        {
            return preference;
        }

        var sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return sys is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
    }

    public void SetLanguage(string preference)
    {
        var lang = Resolve(preference);
        _fallback = Load("en");
        _strings = lang == "en" ? _fallback : Load(lang);
        Language = lang;
        CultureInfo.CurrentUICulture = new CultureInfo(lang == "ru" ? "ru-RU" : "en-US");
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    internal static Dictionary<string, string> Load(string lang)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Bydlo.App/Assets/Strings/{lang}.json"));
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }
}

/// <summary>XAML: <c>Text="{l:T home.connect}"</c> — live-updating localized text.</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
