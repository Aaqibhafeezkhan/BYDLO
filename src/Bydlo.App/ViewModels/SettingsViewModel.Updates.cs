using System.Globalization;
using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using Bydlo.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>BYDLO and engine update checks via GitHub Releases (issue #19, visibility in #456).</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _autoUpdateApp;

    private CancellationTokenSource? _automaticUpdateCheck;

    partial void OnAutoUpdateAppChanged(bool value)
    {
        Services.Settings.AutoUpdateApp = value;
        Services.Settings.CheckForUpdates = value;
        if (!value) { _automaticUpdateCheck?.Cancel(); }
        OnPropertyChanged(nameof(CanDownloadUpdate));
        Persist();
    }

    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private int _updateProgress;

    [ObservableProperty]
    private string _updateStatus = "";

    /// <summary>Secondary line: last check time, live progress or why updates are unavailable.</summary>
    [ObservableProperty]
    private string _updateDetail = "";

    /// <summary>False for archives, portable layouts and Mock Mode; the opt-in switch must say so.</summary>
    [ObservableProperty]
    private bool _isAutoUpdateAvailable = true;

    [ObservableProperty]
    private string? _updateUrl;

    public bool HasUpdateUrl => UpdateUrl is not null;

    public bool CanDownloadUpdate => IsAutoUpdateAvailable && AutoUpdateApp && !IsDownloadingUpdate && !IsCheckingUpdates;

    partial void OnUpdateUrlChanged(string? value) => OnPropertyChanged(nameof(HasUpdateUrl));

    partial void OnIsDownloadingUpdateChanged(bool value) => OnPropertyChanged(nameof(CanDownloadUpdate));

    partial void OnIsCheckingUpdatesChanged(bool value) => OnPropertyChanged(nameof(CanDownloadUpdate));

    partial void OnIsAutoUpdateAvailableChanged(bool value) => OnPropertyChanged(nameof(CanDownloadUpdate));

    partial void OnCheckForUpdatesChanged(bool value)
    {
        Services.Settings.CheckForUpdates = value;
        Persist();
    }

    /// <summary>Refreshes availability and the last-check line; safe to call on every startup.</summary>
    public void RefreshUpdateState()
    {
        IsAutoUpdateAvailable = !Services.IsMock && !NetworkSafetyGuard.IsLocked &&
            Services.AutomaticUpdates.IsAvailable();
        RefreshUpdateDetail();
    }

    private void RefreshUpdateDetail()
    {
        if (!IsAutoUpdateAvailable)
        {
            UpdateDetail = L["settings.update.detail.unavailable"];
            return;
        }
        var last = Services.Settings.LastAutomaticUpdateCheck ?? Services.Settings.LastUpdateCheck;
        UpdateDetail = last is { } value
            ? Loc.T("settings.update.detail.checked", value.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture))
            : L["settings.update.detail.never"];
    }

    [RelayCommand]
    private async Task CheckUpdates()
    {
        if (IsCheckingUpdates)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            if (Services.IsMock || NetworkSafetyGuard.IsLocked)
            {
                throw new InvalidOperationException(L["settings.update.locked"]);
            }
            IsCheckingUpdates = true;
            UpdateUrl = null;
            UpdateStatus = L["settings.update.checking"];
            try
            {
                var client = new GitHubUpdateClient();
                var release = await client.CheckBydloAsync(Version, Services.ShutdownToken);
                Services.Settings.LastUpdateCheck = DateTimeOffset.UtcNow;
                await Services.SaveSettingsAsync();
                if (release is null)
                {
                    UpdateStatus = await CheckEnginesAsync(client);
                }
                else
                {
                    UpdateUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                        ? "https://github.com/endercodezz/BYDLO/releases"
                        : release.HtmlUrl;
                    UpdateStatus = Loc.T("settings.update.available", release.Tag);
                    Services.Settings.PendingUpdate = release;
                    await Services.SaveSettingsAsync();
                    Shell.ShowReleaseNotification(release);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                UpdateStatus = L["settings.update.error"];
            }
            finally
            {
                IsCheckingUpdates = false;
                RefreshUpdateDetail();
            }
        });
    }

    /// <summary>Explicit download: bypasses the daily window so the user never waits a day for a release.</summary>
    [RelayCommand]
    private async Task DownloadUpdate()
    {
        if (IsDownloadingUpdate || IsCheckingUpdates)
        {
            return;
        }
        await Shell.Guard(async () =>
        {
            if (Services.IsMock || NetworkSafetyGuard.IsLocked)
            {
                throw new InvalidOperationException(L["settings.update.locked"]);
            }
            if (!IsAutoUpdateAvailable)
            {
                throw new InvalidOperationException(L["settings.update.detail.unavailable"]);
            }
            IsDownloadingUpdate = true;
            UpdateProgress = 0;
            UpdateStatus = L["settings.update.downloading"];
            UpdateDetail = Loc.T("settings.update.detail.progress", 0);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
            _automaticUpdateCheck = cancellation;
            try
            {
                var progress = new Progress<int>(value =>
                {
                    UpdateProgress = value;
                    UpdateDetail = Loc.T("settings.update.detail.progress", value);
                });
                var ready = await Services.AutomaticUpdates.DownloadNowAsync(
                    Services.Settings, false, progress, cancellation.Token);
                UpdateStatus = ready ? L["settings.update.ready"] : L["settings.update.uptodate"];
                await Services.SaveSettingsAsync();
            }
            catch (UpdateTrustException ex)
            {
                // The signed catalog refused this update: nothing was installed and the working version stays.
                UpdateStatus = L[UpdateTrustMessages.Key(ex.Failure)];
                Services.Log.Warn("update", $"Update refused: {ex.Failure}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                UpdateStatus = L["settings.update.error"];
            }
            finally
            {
                _automaticUpdateCheck = null;
                IsDownloadingUpdate = false;
                UpdateProgress = 0;
                RefreshUpdateDetail();
            }
        });
    }

    private async Task<string> CheckEnginesAsync(GitHubUpdateClient client)
    {
        var pending = new List<string>();
        foreach (var entry in Services.Lock.Engines)
        {
            if (entry.Id.Equals("tg-ws-proxy", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                var release = await client.CheckEngineAsync(entry.Upstream, entry.Version, Services.ShutdownToken);
                if (release is not null)
                {
                    pending.Add($"{entry.Id} {release.Tag}");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return L["settings.update.error"];
            }
        }
        return pending.Count == 0 ? L["settings.update.uptodate"] : Loc.T("settings.update.engines", string.Join(", ", pending));
    }

    /// <summary>Scheduled check: at most once per 24 hours, only when enabled and allowed.</summary>
    public async Task CheckForUpdatesIfDueAsync()
    {
        RefreshUpdateState();
        if (!Services.Settings.AutoUpdateApp || Services.IsMock || NetworkSafetyGuard.IsLocked || IsCheckingUpdates)
        {
            return;
        }
        IsCheckingUpdates = true;
        using var automaticCancellation = CancellationTokenSource.CreateLinkedTokenSource(Services.ShutdownToken);
        _automaticUpdateCheck = automaticCancellation;
        try
        {
            var progress = new Progress<int>(value =>
            {
                UpdateProgress = value;
                UpdateDetail = Loc.T("settings.update.detail.progress", value);
            });
            if (await Services.AutomaticUpdates.DownloadIfDueAsync(Services.Settings,
                    Services.IsMock || NetworkSafetyGuard.IsLocked, progress, automaticCancellation.Token))
            {
                UpdateStatus = L["settings.update.ready"];
            }
            if (!Services.Settings.AutoUpdateApp || automaticCancellation.IsCancellationRequested) { return; }
            var release = await new StartupUpdateChecker(new GitHubUpdateClient()).CheckAsync(
                Services.Settings, Version, Services.IsMock || NetworkSafetyGuard.IsLocked, automaticCancellation.Token);
            await Services.SaveSettingsAsync();
            if (release is not null)
            {
                UpdateUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                    ? "https://github.com/endercodezz/BYDLO/releases"
                    : release.HtmlUrl;
                UpdateStatus = Loc.T("settings.update.available", release.Tag);
                Shell.ShowReleaseNotification(release);
            }
        }
        catch (UpdateTrustException ex)
        {
            // A scheduled download that fails verification must say so instead of looking like a network error.
            UpdateStatus = L[UpdateTrustMessages.Key(ex.Failure)];
            Services.Log.Warn("update", $"Update refused: {ex.Failure}.");
        }
        catch (Exception ex)
        {
            Services.Log.Warn("update", ex.GetType().Name);
        }
        finally
        {
            _automaticUpdateCheck = null;
            IsCheckingUpdates = false;
            RefreshUpdateDetail();
        }
    }
}
