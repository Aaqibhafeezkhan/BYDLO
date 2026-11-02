using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using Bydlo.App.Services;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Engines.TgWsProxy;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class HomeViewModel : PageViewModel
{
    private readonly DispatcherTimer _clock;
    private CancellationTokenSource? _checkCts;
    private int _checkVersion;

    public HomeViewModel(MainViewModel shell)
        : base(shell)
    {
        shell.PropertyChanged += OnShellChanged;
        shell.Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasProfiles));
        };
        L.PropertyChanged += (_, _) => RefreshAll();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(Elapsed)));
        _clock.Start();
    }

    private ConnectionStatus Status => Shell.Primary.Status;

    public bool IsConnected => Status == ConnectionStatus.Connected;

    public bool IsBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Disconnecting or ConnectionStatus.Reconnecting;

    public bool IsError => Status == ConnectionStatus.Error;

    public bool IsEmpty => Shell.Profiles.Count == 0;

    public bool CanCheckConnection => IsConnected && !Services.IsMock && !NetworkSafetyGuard.IsLocked;

    public bool CanRunCheck => CanCheckConnection && !IsChecking;

    public string? CheckDisabledReason => !IsConnected
        ? null
        : Services.IsMock
            ? L["home.check.mock"]
            : NetworkSafetyGuard.IsLocked ? L["home.check.locked"] : null;

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => L["status.connected"],
        ConnectionStatus.Connecting => L["status.connecting"],
        ConnectionStatus.Disconnecting => L["status.disconnecting"],
        ConnectionStatus.Reconnecting => L["status.reconnecting"],
        ConnectionStatus.Error => L["status.error"],
        _ => L["status.disconnected"],
    };

    public string ButtonText => IsConnected || IsBusy || Shell.Primary.CleanupPending ? L["home.disconnect"] : L["home.connect"];

    public string Hint
    {
        get
        {
            if (IsError) return L["home.hint.error"];
            if (IsBusy) return L["home.hint.busy"];
            if (!IsConnected)
            {
                return Shell.SelectedProfile is null ? L["home.hint.noprofile"] : L["home.hint.ready"];
            }

            if (Shell.Primary.IsMock) return L["home.hint.mock"];
            if (Shell.Primary.Mode == ConnectionMode.Tun)
            {
                return L["home.hint.connected"];
            }

            return Shell.Primary.Engine switch
            {
                EngineKind.SingBox => Loc.T("home.hint.proxy", Shell.Primary.LocalProxyPort),
                EngineKind.Xray => Loc.T("home.hint.proxy.xray", Shell.Primary.LocalProxyPort, Shell.Primary.LocalProxyPort + 1),
                _ => L["home.hint.connected"],
            };
        }
    }

    public string? Error => IsError && Shell.Primary.Error is { } error ? Loc.Error(error, Shell.Primary.ErrorLocalizationKey) : null;

    public string Elapsed => IsConnected && Shell.Primary.ConnectedSince is { } since ? Format.Duration(DateTimeOffset.Now - since) : "00:00";

    public string DownSpeed => Format.Speed(Shell.Primary.LastTraffic?.DownloadBytesPerSecond ?? 0);

    public string UpSpeed => Format.Speed(Shell.Primary.LastTraffic?.UploadBytesPerSecond ?? 0);

    public string EngineLabel => Labels.Engine(Shell.Primary.Engine);

    public bool IsTun
    {
        get => Services.Settings.Mode == ConnectionMode.Tun;
        set
        {
            if (value == IsTun) return;
            Services.Settings.Mode = value ? ConnectionMode.Tun : ConnectionMode.LocalProxy;
            _ = Services.SaveSettingsAsync();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Hint));
            OnPropertyChanged(nameof(IsProxy));
            OnPropertyChanged(nameof(IsWholeDevice));
            OnPropertyChanged(nameof(ModeHint));
        }
    }

    public bool HasProfiles => Shell.Profiles.Count > 0;

    public bool IsProxy
    {
        get => !IsTun;
        set
        {
            if (value) IsTun = false;
        }
    }

    public bool IsWholeDevice
    {
        get => IsTun;
        set
        {
            if (value) IsTun = true;
        }
    }

    public string ModeHint => IsTun ? L["home.tun.hint"] : Loc.T("home.mode.proxy.hint", Services.Settings.LocalProxyPort);

    [ObservableProperty]
    private bool _showModeHint;

    [RelayCommand]
    private void ToggleModeHint() => ShowModeHint = !ShowModeHint;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _checkFailed;

    [ObservableProperty]
    private IpCheckResult? _checkResult;

    public bool HasCheckResult => CheckResult is not null;

    public bool HasCheckError => CheckFailed;

    public string? CheckError => CheckFailed ? L["home.check.error"] : null;

    public string CheckIp => CheckResult?.Ip ?? "—";

    public string CheckCountry
    {
        get
        {
            var result = CheckResult;
            if (result is null)
            {
                return "—";
            }

            if (!string.IsNullOrWhiteSpace(result.CountryCode))
            {
                try
                {
                    var localized = new RegionInfo(result.CountryCode).DisplayName;
                    if (!string.IsNullOrWhiteSpace(localized))
                    {
                        return localized;
                    }
                }
                catch (ArgumentException)
                {
                    // Some providers return non-ISO codes; fall back to the provider name below.
                }
            }

            return !string.IsNullOrWhiteSpace(result.CountryName) ? result.CountryName : L["home.check.unknown"];
        }
    }

    public string CheckLatency => CheckResult is null ? "—" : Format.Latency(CheckResult.Latency);

    public string CheckButtonText => IsChecking ? L["home.check.checking"] : L["home.check"];

    partial void OnIsCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunCheck));
        OnPropertyChanged(nameof(CheckButtonText));
    }

    partial void OnCheckFailedChanged(bool value)
    {
        OnPropertyChanged(nameof(CheckError));
        OnPropertyChanged(nameof(HasCheckError));
    }

    partial void OnCheckResultChanged(IpCheckResult? value)
    {
        OnPropertyChanged(nameof(HasCheckResult));
        OnPropertyChanged(nameof(CheckIp));
        OnPropertyChanged(nameof(CheckCountry));
        OnPropertyChanged(nameof(CheckLatency));
    }

    public bool ZapretOn => Shell.Zapret.CleanupPending || Shell.Zapret.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string ZapretState => ToolState(Shell.Zapret, Services.Settings.Zapret.Strategy is { } s ? Path.GetFileNameWithoutExtension(s) : L["tools.zapret.nostrategy"]);

    public bool TgOn => Shell.Tg.CleanupPending || Shell.Tg.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting;

    public string TgState => ToolState(Shell.Tg, $"{Services.Settings.TgWsProxy.Host}:{Services.Settings.TgWsProxy.Port}");

    [RelayCommand]
    private async Task ToggleConnection()
    {
        if (IsConnected || IsBusy || Shell.Primary.CleanupPending)
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
    private async Task CheckConnection()
    {
        if (!CanCheckConnection || IsChecking)
        {
            return;
        }

        ResetCheck();
        var version = _checkVersion;
        var provider = Services.Settings.IpCheckProvider;
        using var cts = new CancellationTokenSource();
        _checkCts = cts;
        IsChecking = true;
        try
        {
            var result = await Services.ConnectionCheck.CheckAsync(
                provider,
                ConnectionCheckService.ResolveProxyPort(Shell.Primary.Engine, Shell.Primary.Mode, Shell.Primary.LocalProxyPort),
                cts.Token);

            if (version != _checkVersion || cts.IsCancellationRequested)
            {
                return;
            }

            CheckResult = result;
            CheckFailed = false;
            Services.Log.Info("check", $"External IP check via {provider} succeeded in {(int)result.Latency.TotalMilliseconds} ms.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version != _checkVersion || cts.IsCancellationRequested)
            {
                return;
            }

            Services.Log.Warn("check", $"External IP check failed: {ex.Message}");
            CheckFailed = true;
        }
        finally
        {
            if (ReferenceEquals(_checkCts, cts))
            {
                _checkCts = null;
                IsChecking = false;
            }
        }
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
        if (e.PropertyName is nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.IsMock))
        {
            ResetCheck();
        }

        if (e.PropertyName is nameof(MainViewModel.Primary) or nameof(MainViewModel.SelectedProfile) or nameof(MainViewModel.IsMock)
            or nameof(MainViewModel.Zapret) or nameof(MainViewModel.Tg))
        {
            RefreshAll();
        }
    }

    public void RefreshAll()
    {
        if (!IsConnected)
        {
            ResetCheck();
        }

        foreach (var name in new[]
                 {
                     nameof(IsConnected), nameof(IsBusy), nameof(IsError), nameof(IsEmpty), nameof(StatusText), nameof(ButtonText), nameof(Hint), nameof(Error),
                     nameof(Elapsed), nameof(DownSpeed), nameof(UpSpeed), nameof(EngineLabel), nameof(ZapretOn), nameof(ZapretState), nameof(TgOn), nameof(TgState),
                     nameof(IsTun), nameof(IsProxy), nameof(IsWholeDevice), nameof(ModeHint), nameof(HasProfiles), nameof(CanCheckConnection), nameof(CanRunCheck), nameof(CheckDisabledReason), nameof(CheckButtonText),
                     nameof(CheckError), nameof(HasCheckError), nameof(CheckIp), nameof(CheckCountry), nameof(CheckLatency), nameof(HasCheckResult),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Clears the card when Settings changes the provider. Safe to call from the UI thread.</summary>
    public void ResetConnectionCheck() => ResetCheck();

    private void ResetCheck()
    {
        _checkVersion++;
        _checkCts?.Cancel();
        _checkCts = null;
        IsChecking = false;
        CheckResult = null;
        CheckFailed = false;
    }
}
