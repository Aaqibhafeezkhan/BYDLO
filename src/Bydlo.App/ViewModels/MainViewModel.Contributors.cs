using Bydlo.Core.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.App.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>Runs after the update check so both never save settings at the same time.</summary>
    private async Task RunStartupChecksAsync()
    {
        await Settings.CheckForUpdatesIfDueAsync();
        await RefreshContributorsAsync();
    }

    private async Task RefreshContributorsAsync()
    {
        var blocked = Services.IsMock || NetworkSafetyGuard.IsLocked;
        var before = Services.Settings.LastContributorsCheck;
        try
        {
            var list = await new ContributorsRefresher(new GitHubContributorsClient())
                .RefreshAsync(Services.Settings, blocked, Services.ShutdownToken);
            Contributors.Show(list);
            if (Services.Settings.LastContributorsCheck != before)
            {
                await Services.SaveSettingsAsync();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Services.Log.Warn("contributors", ex.GetType().Name);
        }
    }
}
