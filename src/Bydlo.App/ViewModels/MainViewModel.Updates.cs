using Bydlo.Core.Diagnostics;
using Bydlo.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Bydlo.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReleaseNotification))]
    [NotifyPropertyChangedFor(nameof(ReleaseNotificationText))]
    private GitHubRelease? _releaseNotification;

    public bool HasReleaseNotification => ReleaseNotification is not null;
    public string ReleaseNotificationText => ReleaseNotification is { } release
        ? Loc.T("settings.update.available", release.Tag) : "";

    /// <summary>Release banners are notification content, so they need the notifications consent (issue #438).</summary>
    public void ShowReleaseNotification(GitHubRelease release)
    {
        if (!Services.Settings.ConsentNotifications) { return; }
        ReleaseNotification = release;
    }

    [RelayCommand]
    private async Task DismissReleaseNotification()
    {
        ReleaseNotification = null;
        Services.Settings.PendingUpdate = null;
        await Guard(() => Services.SaveSettingsAsync());
    }

    [RelayCommand]
    private async Task OpenReleaseNotification()
    {
        if (ReleaseNotification is not { } release || Services.TopLevel()?.Launcher is not { } launcher)
        {
            return;
        }
        await Guard(async () =>
        {
            // Construct a repository release URL rather than trusting a stored/external URL.
            var uri = new Uri("https://github.com/endercodezz/BYDLO/releases/tag/" + Uri.EscapeDataString(release.Tag));
            if (await launcher.LaunchUriAsync(uri))
            {
                await DismissReleaseNotification();
            }
        });
    }
}
