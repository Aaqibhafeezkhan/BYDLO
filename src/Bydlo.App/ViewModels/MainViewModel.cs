using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
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

    public MainViewModel(AppServices services)
    {
        Services = services;
        L.PropertyChanged += (_, _) =>
        {
            if (Toast is not null && _toastLocalizationKey is not null)
            {
                Toast = Loc.Error(Toast, _toastLocalizationKey);
            }
        };
        ReloadProfiles();

        Home = new HomeViewModel(this);
        Connections = new ConnectionsViewModel(this);
        Tools = new ToolsViewModel(this);
        Routing = new RoutingViewModel(this);
        Statistics = new StatisticsViewModel(this);
        Settings = new SettingsViewModel(this);
        NavItems =
        [
            new("home", "nav.home", "IconHome", Home),
            new("connections", "nav.connections", "IconServers", Connections),
            new("tools", "nav.tools", "IconTools", Tools),
            new("routing", "nav.routing", "IconRouting", Routing),
            new("statistics", "nav.statistics", "IconStats", Statistics),
            new("settings", "nav.settings", "IconSettings", Settings),
        ];
        _currentNav = NavItems[0];
        _currentNav.IsSelected = true;

        services.Connections.SessionChanged += (_, s) => Dispatcher.UIThread.Post(() => OnSessionChanged(s));
        _hasOrphans = services.Orphans.Count > 0;
    }

    public AppServices Services { get; }

    public HomeViewModel Home { get; }

    public ConnectionsViewModel Connections { get; }

    public ToolsViewModel Tools { get; }

    public RoutingViewModel Routing { get; }

    public StatisticsViewModel Statistics { get; }

    public SettingsViewModel Settings { get; }

    public IReadOnlyList<NavItem> NavItems { get; }

    public ObservableCollection<ProfileItem> Profiles { get; } = [];

    /// <summary>Last 60 seconds of download / upload speed (bytes/s) for charts.</summary>
    public ObservableCollection<double> DownHistory { get; } = [];

    public ObservableCollection<double> UpHistory { get; } = [];

    [ObservableProperty]
    private NavItem _currentNav;

    [ObservableProperty]
    private bool _isCompact;

    [ObservableProperty]
    private ProfileItem? _selectedProfile;

    [ObservableProperty]
    private SessionSnapshot _primary = new(ConnectionManager.PrimaryKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _zapret = new(ConnectionManager.ZapretKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private SessionSnapshot _tg = new(ConnectionManager.TgWsProxyKey, ConnectionStatus.Disconnected, null, null, false, null, null, null);

    [ObservableProperty]
    private bool _isMock;

    [ObservableProperty]
    private bool _hasOrphans;

    [ObservableProperty]
    private string? _toast;

    [ObservableProperty]
    private bool _toastIsError;

    public PageViewModel CurrentPage => CurrentNav.Page;

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

    public void ReloadProfiles()
    {
        var activeId = Primary.Profile?.Id;
        var items = Services.Profiles.Profiles
            .OrderByDescending(p => p.IsFavorite)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ProfileItem(p) { IsActive = p.Id == activeId })
            .ToList();
        var selectedId = SelectedProfile?.Id ?? Services.Settings.LastProfileId;
        Profiles.ReplaceWith(items);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        IsMock = Services.IsMock;
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
