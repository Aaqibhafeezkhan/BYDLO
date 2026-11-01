using System.ComponentModel;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Models;
using Bydlo.Engines.TgWsProxy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly DispatcherTimer _clock;

    public HomeViewModel(MainViewModel shell)
        : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        L.PropertyChanged += (_, _) => RefreshAll();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(Elapsed)));
        _clock.Start();
    }

    private ConnectionStatus Status => Shell.Primary.Status;

    public bool IsConnected => Status == ConnectionStatus.Connected;

    public bool IsBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting or ConnectionStatus.Reconnecting;

    public bool IsError => Status == ConnectionStatus.Error;

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => L["status.connected"],
        ConnectionStatus.Connecting => L["status.connecting"],
        ConnectionStatus.Disconnecting => L["status.disconnecting"],
        ConnectionStatus.Reconnecting => L["status.reconnecting"],
        ConnectionStatus.Error => L["status.error"],
        _ => L["status.disconnected"],
    };

    public string ButtonText => IsConnected || IsBusy ? L["home.disconnect"] : L["home.connect"];

    public string Hint => IsConnected
        ? (Services.Settings.Mode == ConnectionMode.LocalProxy && !Shell.Primary.IsMock
            ? Loc.T("home.hint.proxy", Services.Settings.LocalProxyPort)
            : L["home.hint.connected"])
        : Shell.SelectedProfile is null ? L["home.hint.noprofile"] : L["home.hint.tap"];

    public string? Error => IsError ? Shell.Primary.Error : null;

    public string Elapsed => IsConnected && Shell.Primary.ConnectedSince is { } since ? Format.Duration(DateTimeOffset.Now - since) : "00:00";

    public string DownSpeed => Format.Speed(Shell.Primary.LastTraffic?.DownloadBytesPerSecond ?? 0);

    public string UpSpeed => Format.Speed(Shell.Primary.LastTraffic?.UploadBytesPerSecond ?? 0);

    public string EngineLabel => Labels.Engine(Shell.Primary.Engine);

    public bool IsTun
    {
        get => Services.Settings.Mode == ConnectionMode.Tun;
        set
        {
            Services.Settings.Mode = value ? ConnectionMode.Tun : ConnectionMode.LocalProxy;
            _ = Services.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Hint));
        }
    }

    public bool ZapretOn => Shell.Zapret.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string ZapretState => ToolState(Shell.Zapret, Services.Settings.Zapret.Strategy is { } s ? Path.GetFileNameWithoutExtension(s) : L["tools.zapret.nostrategy"]);

    public bool TgOn => Shell.Tg.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string TgState => ToolState(Shell.Tg, $"{Services.Settings.TgWsProxy.Host}:{Services.Settings.TgWsProxy.Port}");

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected || IsBusy)
        {
            await Shell.Guard(() => Services.Connections.DisconnectAsync());
            return;
        }

        if (Shell.SelectedProfile is not { } item)
        {
            Shell.ShowToast(L["home.hint.noprofile"], isError: true);
            Shell.Navigate("connections");
            return;
        }

        foreach (var w in Services.Connections.PreviewConflicts(item.Profile, Services.EffectiveSettings).Where(i => i.Severity == ConflictSeverity.Warning))
        {
            Shell.ShowToast(w.Message);
        }

        await Shell.Guard(() => Services.Connections.ConnectAsync(item.Profile, Services.EffectiveSettings));
    }

    [RelayCommand]
    private Task ToggleZapret() => Shell.Tools.ToggleZapretCommand.ExecuteAsync(null);

    [RelayCommand]
    private Task ToggleTg() => Shell.Tools.ToggleTgCommand.ExecuteAsync(null);

    [RelayCommand]
    private async Task CopyTgLink()
    {
        var secret = await Services.TgSecrets.GetOrCreateAsync();
        var s = Services.Settings.TgWsProxy;
        var link = TgLinkBuilder.Build(s.Host, s.Port, secret, s.FakeTlsDomain);
        if (Services.TopLevel()?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(link);
            Shell.ShowToast(L["toast.copied"]);
        }
    }

    private string ToolState(SessionSnapshot s, string idle) => s.Status switch
    {
        ConnectionStatus.Connected => L["status.running"],
        ConnectionStatus.Connecting => L["status.starting"],
        ConnectionStatus.Error => L["status.error"],
        _ => idle,
    };

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Primary) or nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.Zapret) or nameof(MainViewModel.Tg))
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        foreach (var name in new[]
                 {
                     nameof(IsConnected), nameof(IsBusy), nameof(IsError), nameof(StatusText), nameof(ButtonText), nameof(Hint), nameof(Error),
                     nameof(Elapsed), nameof(DownSpeed), nameof(UpSpeed), nameof(EngineLabel), nameof(ZapretOn), nameof(ZapretState), nameof(TgOn), nameof(TgState),
                     nameof(IsTun),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}
