using Bydlo.App.Services;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

/// <summary>BYDLO and engine update checks via GitHub Releases (issue #19).</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _checkForUpdates;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private string? _updateUrl;

    public bool HasUpdateUrl => UpdateUrl is not null;

    partial void OnUpdateUrlChanged(string? value) => OnPropertyChanged(nameof(HasUpdateUrl));

    partial void OnCheckForUpdatesChanged(bool value)
    {
        Services.Settings.CheckForUpdates = value;
        Persist();
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
        if (!Services.Settings.CheckForUpdates || Services.IsMock || NetworkSafetyGuard.IsLocked || IsCheckingUpdates)
        {
            return;
        }
        IsCheckingUpdates = true;
        try
        {
            var release = await new StartupUpdateChecker(new GitHubUpdateClient()).CheckAsync(
                Services.Settings, Version, Services.IsMock || NetworkSafetyGuard.IsLocked, Services.ShutdownToken);
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
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Services.Log.Warn("update", ex.GetType().Name);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }
}
