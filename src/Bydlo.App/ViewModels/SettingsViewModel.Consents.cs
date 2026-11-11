using CommunityToolkit.Mvvm.ComponentModel;

namespace Bydlo.App.ViewModels;

/// <summary>
/// The three independent GitHub consents from the first-run screen (issue #438). Revoking one stops
/// only its own background requests; the other two keep working.
/// </summary>
public sealed partial class SettingsViewModel
{
    private bool _updatingConsents;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadUpdate))]
    [NotifyPropertyChangedFor(nameof(CanCheckUpdates))]
    private bool _consentAutomaticUpdates;

    [ObservableProperty]
    private bool _consentNotifications;

    [ObservableProperty]
    private bool _consentThemes;

    /// <summary>Re-reads consents changed elsewhere, e.g. by the first-run screen or a backup import.</summary>
    public void RefreshConsents()
    {
        _updatingConsents = true;
        ConsentAutomaticUpdates = Services.Settings.ConsentAutomaticUpdates;
        ConsentNotifications = Services.Settings.ConsentNotifications;
        ConsentThemes = Services.Settings.ConsentThemes;
        _updatingConsents = false;
        // The update line explains why updates are unavailable, so it must follow the consent.
        RefreshUpdateDetail();
    }

    partial void OnConsentAutomaticUpdatesChanged(bool value)
    {
        if (_updatingConsents) { return; }
        Services.Settings.ConsentAutomaticUpdates = value;
        // Keep the legacy "check for updates" preference in step so a revoked consent cannot leave
        // a scheduled check behind (issue #438).
        CheckForUpdates = value;
        if (!value)
        {
            // A package that is already staged must not be applied after the consent is revoked.
            AutoUpdateApp = false;
            _automaticUpdateCheck?.Cancel();
        }
        RefreshUpdateDetail();
        Persist();
    }

    partial void OnConsentNotificationsChanged(bool value)
    {
        if (_updatingConsents) { return; }
        Services.Settings.ConsentNotifications = value;
        Persist();
    }

    partial void OnConsentThemesChanged(bool value)
    {
        if (_updatingConsents) { return; }
        Services.Settings.ConsentThemes = value;
        // A revoked consent also drops the catalog that was already parsed.
        if (!value) { GalleryThemes.Clear(); }
        Persist();
    }
}
