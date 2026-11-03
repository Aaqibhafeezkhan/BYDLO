using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string titleKey, string iconKey, PageViewModel page)
    {
        Key = key;
        TitleKey = titleKey;
        IconKey = iconKey;
        Page = page;
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ShortTitle));
        };
    }

    public string Key { get; }

    public string TitleKey { get; }

    public string IconKey { get; }

    public PageViewModel Page { get; }

    public string Title => Loc.Instance[TitleKey];

    /// <summary>Compact label for the phone bottom bar.</summary>
    public string ShortTitle => Loc.Instance[TitleKey + ".short"];

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Shell: navigation, shared profile list, live session state, toasts.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private const int HistoryLength = 60;
    private int _toastVersion;
    private string? _toastLocalizationKey;

    public MainViewModel(AppServices services, Bydlo.Core.Import.SubscriptionClient? subscriptionClient = null)
    {
        Services = services;
        L.PropertyChanged += (_, _) =>
        {
            if (Toast is not null && _toastLocalizationKey is not null)
            {
                Toast = Loc.Error(Toast, _toastLocalizationKey);
            }

            foreach (var c in Chains)
            {
                c.RefreshDetails();
            }
        };
        _isOnboardingVisible = !services.Settings.OnboardingCompleted;
        _onboardingLanguage = services.Settings.Language;
        _onboardingTheme = services.Settings.Theme;
        ReloadProfiles();
        ReloadChains();

        Home = new HomeViewModel(this);
        Connections = new ConnectionsViewModel(this);
        AddServerWizard = new AddServerWizardViewModel(this, subscriptionClient);
        ChainEditor = new ChainEditorViewModel(this);
        Tools = new ToolsViewModel(this);
        Routing = new RoutingViewModel(this);
        Statistics = new StatisticsViewModel(this);
        Settings = new SettingsViewModel(this);
        ActiveConnections = new ActiveConnectionsViewModel(this);
        NavItems =
        [
            new("home", "nav.home", "LineHome", Home),
            new("connections", "nav.connections", "LineServers", Connections),
            new("tools", "nav.tools", "LineTools", Tools),
            new("routing", "nav.routing", "LineRouting", Routing),
            new("statistics", "nav.statistics", "LineStats", Statistics),
            new("settings", "nav.settings", "LineSettings", Settings),
        ];
        _currentNav = NavItems[0];
        _currentNav.IsSelected = true;

        services.Connections.SessionChanged += (_, s) => Dispatcher.UIThread.Post(() => OnSessionChanged(s));
        Chains.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasChains));
            OnPropertyChanged(nameof(HasNoChains));
        };
        _hasOrphans = services.Orphans.Count > 0;
        _ = Settings.CheckForUpdatesIfDueAsync();
    }

    public AppServices Services { get; }

    public HomeViewModel Home { get; }

    public ConnectionsViewModel Connections { get; }

    public AddServerWizardViewModel AddServerWizard { get; }

    [ObservableProperty] private bool _isAddServerOpen;

    [RelayCommand]
    private void OpenAddServer()
    {
        if (AddServerWizard.Busy || HasImportPreview || IsImportBusy) { return; }
        DismissClipboardOffer();
        AddServerWizard.Reset();
        IsAddServerOpen = true;
    }

    [RelayCommand]
    private async Task OpenImport(string source)
    {
        if (IsAddServerOpen || AddServerWizard.Busy || HasImportPreview || IsImportBusy) { return; }
        OpenAddServer();
        if (!IsAddServerOpen || AddServerWizard.Busy) { return; }
        switch (source)
        {
            case "file":
                AddServerWizard.ChooseFileCommand.Execute(null);
                await AddServerWizard.SelectFileCommand.ExecuteAsync(null);
                break;
            case "qr":
                AddServerWizard.ChooseQrCommand.Execute(null);
                await AddServerWizard.SelectQrCommand.ExecuteAsync(null);
                break;
            default:
                AddServerWizard.ChooseLinkCommand.Execute(null);
                if (source == "clipboard") { await AddServerWizard.PasteCommand.ExecuteAsync(null); }
                break;
        }
    }

    public ChainEditorViewModel ChainEditor { get; }

    [ObservableProperty] private bool _isChainEditorOpen;

    [RelayCommand]
    private void NewChain()
    {
        if (IsChainEditorOpen) { return; }
        ChainEditor.Create();
        IsChainEditorOpen = true;
    }

    [RelayCommand]
    private void EditChain(ChainItem item)
    {
        if (IsChainEditorOpen) { return; }
        ChainEditor.Edit(item);
        IsChainEditorOpen = true;
    }

    public ToolsViewModel Tools { get; }

    public RoutingViewModel Routing { get; }

    public StatisticsViewModel Statistics { get; }

    public SettingsViewModel Settings { get; }

    public ActiveConnectionsViewModel ActiveConnections { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public static IReadOnlyList<string> OnboardingLanguages { get; } = ["auto", "en", "ru"];

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>Relay chains (issue #75), entry → exit.</summary>
    public ObservableCollection<ChainItem> Chains { get; } = [];

    public bool HasChains => Chains.Count > 0;

    public bool HasNoChains => Chains.Count == 0;

    [ObservableProperty]
    private ChainItem? _selectedChain;

    /// <summary>Last latency probe per profile id (issue #10). Empty until the user checks.</summary>
    public Dictionary<Guid, ServerLatencyResult> Latencies { get; } = new();

    public bool HasLatencyResults => Latencies.Count > 0;

    [ObservableProperty]
    private bool _sortByLatency;

    partial void OnSortByLatencyChanged(bool value) => ReloadProfiles();

    /// <summary>Last 60 seconds of download / upload speed (bytes/s) for charts.</summary>
    public ObservableCollection<double> DownHistory { get; } = [];

    public ObservableCollection<double> UpHistory { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private NavItem _currentNav;

    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    private ProfileItem? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private SessionSnapshot _primary = new(ConnectionManager.PrimaryKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _zapret = new(ConnectionManager.ZapretKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _tg = new(ConnectionManager.TgWsProxyKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private bool _isMock;

    [ObservableProperty]
    private bool _isOnboardingVisible;

    [ObservableProperty]
    private string _onboardingLanguage = "auto";

    [ObservableProperty]
    private ThemePreference _onboardingTheme = ThemePreference.System;

    public bool OnboardingThemePurple { get => OnboardingTheme == ThemePreference.Purple; set { if (value) { OnboardingTheme = ThemePreference.Purple; } } }

    public bool OnboardingThemeLight { get => OnboardingTheme == ThemePreference.Light; set { if (value) { OnboardingTheme = ThemePreference.Light; } } }

    public bool OnboardingThemeOled { get => OnboardingTheme == ThemePreference.Oled; set { if (value) { OnboardingTheme = ThemePreference.Oled; } } }

    public bool OnboardingThemeSystem { get => OnboardingTheme == ThemePreference.System; set { if (value) { OnboardingTheme = ThemePreference.System; } } }

    [ObservableProperty]
    private bool _hasOrphans;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private string? _toast;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleToast))]
    private bool _toastIsError;

    public PageViewModel CurrentPage => CurrentNav.Page;

    // A connection error is already visible inline on Home; do not cover the tools with a duplicate toast.
    public bool HasVisibleToast => Toast is not null &&
        !(ToastIsError && CurrentNav.Key == "home" && Home.Error == Toast);

    public bool IsElevated => Services.Privileges.IsElevated;

    partial void OnCurrentNavChanged(NavItem value)
    {
        foreach (var n in NavItems)
        {
            n.IsSelected = n == value;
        }

        OnPropertyChanged(nameof(CurrentPage));
        value.Page.OnShown();
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value is not null && Services.Settings.LastProfileId != value.Id)
        {
            Services.Settings.LastProfileId = value.Id;
            _ = Services.SaveSettingsAsync();
        }
    }

    partial void OnOnboardingLanguageChanged(string value)
    {
        Services.Settings.Language = value;
        Loc.Instance.SetLanguage(value);
        _ = Services.SaveSettingsAsync();
    }

    partial void OnOnboardingThemeChanged(ThemePreference value)
    {
        Services.Settings.Theme = value;
        Services.Theme.Apply(value);
        _ = Services.SaveSettingsAsync();
        OnPropertyChanged(nameof(OnboardingThemePurple));
        OnPropertyChanged(nameof(OnboardingThemeLight));
        OnPropertyChanged(nameof(OnboardingThemeOled));
        OnPropertyChanged(nameof(OnboardingThemeSystem));
    }

    [RelayCommand]
    private void FinishOnboarding()
    {
        Services.Settings.OnboardingCompleted = true;
        IsOnboardingVisible = false;
        _ = Services.SaveSettingsAsync();
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        CurrentNav = NavItems.FirstOrDefault(n => n.Key == key) ?? CurrentNav;
    }

    [RelayCommand]
    private void CleanupOrphans()
    {
        var n = Services.CleanupOrphans();
        HasOrphans = false;
        ShowToast(Loc.T("toast.orphans", n));
    }

    public void RefreshMock() => IsMock = Services.IsMock;

    [ObservableProperty] private bool _sortBySpeed;
    private IReadOnlyDictionary<Guid, long> _serverSpeeds = new Dictionary<Guid, long>();

    partial void OnSortBySpeedChanged(bool value) => ReloadProfiles();

    public void SetServerSpeeds(IReadOnlyDictionary<Guid, long> results)
    {
        _serverSpeeds = results;
        ReloadProfiles();
    }

    public void ReloadProfiles()
    {
        var activeId = Primary.Profile?.Id;
        var ordered = ServerListOrdering.Order(Services.Profiles.Profiles, Latencies, SortByLatency);
        var profiles = SortBySpeed ? ordered.OrderByDescending(p => _serverSpeeds.GetValueOrDefault(p.Id)).ToList() : ordered;
        var items = profiles
            .Select(p => new ProfileItem(p)
            {
                IsActive = p.Id == activeId,
                MeasuredSpeed = _serverSpeeds.TryGetValue(p.Id, out var speed) ? speed : null,
            })
            .ToList();
        foreach (var item in items)
        {
            ApplyLatency(item);
        }

        var selectedId = SelectedProfile?.Id ?? Services.Settings.LastProfileId;
        Profiles.ReplaceWith(items);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        IsMock = Services.IsMock;
        ReloadChains();
    }

    /// <summary>Reloads chain rows and resolves hop names against current profiles.</summary>
    public void ReloadChains()
    {
        var names = Services.Profiles.Profiles.ToDictionary(p => p.Id, p => p.Name);
        var activeId = Primary.Chain?.Id;
        var items = Services.Profiles.Chains
            .OrderByDescending(c => c.IsFavorite)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new ChainItem(c)
            {
                IsActive = c.Id == activeId,
                HopSummary = string.Join(" → ", c.ProfileIds.Select(id => names.TryGetValue(id, out var n) ? n : "?")),
            })
            .ToList();
        foreach (var item in items)
        {
            item.RefreshDetails();
        }

        var selectedId = SelectedChain?.Id;
        Chains.ReplaceWith(items);
        SelectedChain = items.FirstOrDefault(c => c.Id == selectedId);
        OnPropertyChanged(nameof(HasChains));
        OnPropertyChanged(nameof(HasNoChains));
    }

    /// <summary>Stores fresh probe results and refreshes the visible rows (issue #10).</summary>
    public void SetLatencies(IReadOnlyDictionary<Guid, ServerLatencyResult> results)
    {
        Latencies.Clear();
        foreach (var (id, result) in results)
        {
            Latencies[id] = result;
        }

        foreach (var item in Profiles)
        {
            ApplyLatency(item);
        }

        if (SortByLatency)
        {
            ReloadProfiles();
        }

        OnPropertyChanged(nameof(HasLatencyResults));
    }

    private void ApplyLatency(ProfileItem item)
    {
        if (Latencies.TryGetValue(item.Id, out var result))
        {
            item.Latency = result.Reachable ? result.Latency : null;
            item.LatencyFailed = !result.Reachable;
        }
        else
        {
            item.Latency = null;
            item.LatencyFailed = false;
        }
    }

    public void ShowToast(string message, bool isError = false, string? localizationKey = null)
    {
        _toastLocalizationKey = localizationKey;
        message = Loc.Error(message, localizationKey);
        var version = ++_toastVersion;
        Toast = message;
        ToastIsError = isError;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion)
            {
                Toast = null;
            }
        }, TimeSpan.FromSeconds(isError ? 6 : 3.5));
    }

    /// <summary>Runs an action, turning exceptions into an error toast.</summary>
    public async Task<bool> Guard(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is EngineException or InvalidOperationException or IOException or HttpRequestException or NotSupportedException or UnauthorizedAccessException or FormatException or TimeoutException or PlatformNotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            ShowToast(ex.Message, isError: true, localizationKey: (ex as EngineException)?.LocalizationKey);
            return false;
        }
    }

    private void OnSessionChanged(SessionSnapshot s)
    {
        switch (s.Key)
        {
            case ConnectionManager.PrimaryKey:
                var wasConnected = Primary.Status == ConnectionStatus.Connected;
                Primary = s;
                foreach (var p in Profiles)
                {
                    p.IsActive = s.Status == ConnectionStatus.Connected && p.Id == s.Profile?.Id;
                }

                foreach (var c in Chains)
                {
                    c.IsActive = s.Status == ConnectionStatus.Connected && c.Id == s.Chain?.Id;
                }

                if (s.LastTraffic is { } t && s.Status == ConnectionStatus.Connected)
                {
                    Push(DownHistory, t.DownloadBytesPerSecond);
                    Push(UpHistory, t.UploadBytesPerSecond);
                }
                else if (s.Status != ConnectionStatus.Connected && wasConnected)
                {
                    DownHistory.Clear();
                    UpHistory.Clear();
                }

                break;
            case ConnectionManager.ZapretKey:
                Zapret = s;
                break;
            case ConnectionManager.TgWsProxyKey:
                Tg = s;
                break;
        }
    }

    private static void Push(ObservableCollection<double> list, long value)
    {
        list.Add(value);
        while (list.Count > HistoryLength)
        {
            list.RemoveAt(0);
        }
    }
}
