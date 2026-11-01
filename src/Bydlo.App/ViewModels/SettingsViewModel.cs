using Bydlo.App.Services;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed record Credit(string Name, string Author, string License, string Url, string Role);

public sealed partial class SettingsViewModel : PageViewModel
{
    public SettingsViewModel(MainViewModel shell)
        : base(shell)
    {
        var s = Services.Settings;
        _theme = s.Theme;
        _language = s.Language;
        _mockMode = Services.IsMock;
        _mockScenario = s.MockScenario;
        _localPort = s.LocalProxyPort;
        _setSystemProxy = s.SetSystemProxy;
        _autoReconnect = s.AutoReconnect;
        _showAdvanced = s.ShowAdvanced;
    }

    public static IReadOnlyList<string> Languages { get; } = ["auto", "en", "ru"];

    public static IReadOnlyList<MockScenario> Scenarios { get; } = Enum.GetValues<MockScenario>();

    /// <summary>Upstream projects BYDLO builds on. Also listed in THIRD_PARTY_NOTICES.md.</summary>
    public static IReadOnlyList<Credit> Credits { get; } =
    [
        new("zapret-discord-youtube", "Flowseal", "MIT", "https://github.com/Flowseal/zapret-discord-youtube", "Zapret strategies, lists, winws bundle"),
        new("zapret", "bol-van", "MIT", "https://github.com/bol-van/zapret", "winws / nfqws DPI bypass engine"),
        new("WinDivert", "basil00", "LGPL-3.0 / GPL-2.0", "https://github.com/basil00/WinDivert", "Packet interception driver used by winws"),
        new("tg-ws-proxy", "Flowseal", "MIT", "https://github.com/Flowseal/tg-ws-proxy", "Telegram WebSocket bridge proxy"),
        new("sing-box", "SagerNet (nekohasekai)", "GPL-3.0-or-later", "https://github.com/SagerNet/sing-box", "Universal proxy platform"),
        new("Xray-core", "Project X (XTLS)", "MPL-2.0", "https://github.com/XTLS/Xray-core", "VLESS / REALITY / XHTTP"),
        new("WireGuard", "Jason A. Donenfeld / WireGuard LLC", "MIT / GPL-2.0", "https://www.wireguard.com", "WireGuard tunnels"),
        new("AmneziaWG", "Amnezia VPN", "MIT / GPL-2.0", "https://github.com/amnezia-vpn", "Obfuscated WireGuard"),
        new("OpenVPN", "OpenVPN Inc. & community", "GPL-2.0", "https://github.com/OpenVPN/openvpn", "OpenVPN tunnels"),
        new("Avalonia UI", "AvaloniaUI OÜ", "MIT", "https://github.com/AvaloniaUI/Avalonia", "Cross-platform UI framework"),
        new("CommunityToolkit.Mvvm", ".NET Foundation", "MIT", "https://github.com/CommunityToolkit/dotnet", "MVVM helpers"),
        new("ZXing.Net", "Michael Jahn", "Apache-2.0", "https://github.com/micjahn/ZXing.Net", "QR code decoding"),
        new("Inter", "Rasmus Andersson", "OFL-1.1", "https://github.com/rsms/inter", "Typeface"),
        new("Material Design Icons", "Pictogrammers", "Apache-2.0", "https://github.com/Templarian/MaterialDesign", "Icons"),
    ];

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public string DataDirectory => Services.Paths.DataDirectory;

    public string SecretProtection => Services.Secrets.ProtectionDescription;

    public bool IsElevated => Services.Privileges.IsElevated;

    public bool CanRestartElevated => !IsElevated && Services.Privileges.CanRestartElevated;

    public bool MockForcedByFlag => Services.ForcedMock;

    public bool SafetyLock => NetworkSafetyGuard.IsLocked;

    public bool SystemProxySupported => Services.SystemProxy.IsSupported;

    [ObservableProperty]
    private ThemePreference _theme;

    [ObservableProperty]
    private string _language;

    [ObservableProperty]
    private bool _mockMode;

    [ObservableProperty]
    private MockScenario _mockScenario;

    [ObservableProperty]
    private int _localPort;

    [ObservableProperty]
    private bool _setSystemProxy;

    [ObservableProperty]
    private bool _autoReconnect;

    [ObservableProperty]
    private bool _showAdvanced;

    public bool ThemePurple { get => Theme == ThemePreference.Purple; set { if (value) { Theme = ThemePreference.Purple; } } }

    public bool ThemeLight { get => Theme == ThemePreference.Light; set { if (value) { Theme = ThemePreference.Light; } } }

    public bool ThemeOled { get => Theme == ThemePreference.Oled; set { if (value) { Theme = ThemePreference.Oled; } } }

    public bool ThemeSystem { get => Theme == ThemePreference.System; set { if (value) { Theme = ThemePreference.System; } } }

    partial void OnThemeChanged(ThemePreference value)
    {
        Services.Settings.Theme = value;
        Services.Theme.Apply(value);
        Persist();
        OnPropertyChanged(nameof(ThemePurple));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeOled));
        OnPropertyChanged(nameof(ThemeSystem));
    }

    partial void OnLanguageChanged(string value)
    {
        Services.Settings.Language = value;
        Loc.Instance.SetLanguage(value);
        Persist();
    }

    partial void OnMockModeChanged(bool value)
    {
        Services.Settings.MockMode = value;
        Shell.RefreshMock();
        Persist();
    }

    partial void OnMockScenarioChanged(MockScenario value)
    {
        Services.Settings.MockScenario = value;
        Persist();
    }

    partial void OnLocalPortChanged(int value)
    {
        if (value is > 0 and < 65536)
        {
            Services.Settings.LocalProxyPort = value;
            Persist();
        }
    }

    partial void OnSetSystemProxyChanged(bool value)
    {
        Services.Settings.SetSystemProxy = value;
        Persist();
    }

    partial void OnAutoReconnectChanged(bool value)
    {
        Services.Settings.AutoReconnect = value;
        Persist();
    }

    partial void OnShowAdvancedChanged(bool value)
    {
        Services.Settings.ShowAdvanced = value;
        Persist();
    }

    [RelayCommand]
    private async Task RestartElevated()
    {
        await Services.Connections.StopAllAsync();
        if (Services.Privileges.TryRestartElevated(Services.Args))
        {
            Environment.Exit(0);
        }
    }

    [RelayCommand]
    private async Task OpenUrl(string url)
    {
        if (Services.TopLevel()?.Launcher is { } launcher)
        {
            await launcher.LaunchUriAsync(new Uri(url));
        }
    }

    private void Persist() => _ = Services.SaveSettingsAsync();
}
