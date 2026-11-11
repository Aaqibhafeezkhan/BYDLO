namespace Bydlo.Core.Models;

/// <summary>Optional features that read from GitHub (issue #438).</summary>
public enum OptionalFeature
{
    /// <summary>BYDLO release checks and packaged application updates.</summary>
    AutomaticUpdates,

    /// <summary>Notification content from the repository and desktop notifications.</summary>
    Notifications,

    /// <summary>Theme gallery index and theme files.</summary>
    Themes,
}

/// <summary>
/// The three independent consents from the first-run screen (issue #438). A fresh profile has all
/// three off, and each feature is allowed only by its own consent: allowing updates never enables
/// notifications or themes. Consent never replaces the integrity checks on downloaded data.
/// </summary>
public sealed record FeatureConsent(bool AutomaticUpdates, bool Notifications, bool Themes)
{
    /// <summary>Nothing allowed; the state of a fresh profile.</summary>
    public static FeatureConsent None { get; } = new(false, false, false);

    public static FeatureConsent From(AppSettings settings) =>
        new(settings.ConsentAutomaticUpdates, settings.ConsentNotifications, settings.ConsentThemes);

    public bool Allows(OptionalFeature feature) => feature switch
    {
        OptionalFeature.AutomaticUpdates => AutomaticUpdates,
        OptionalFeature.Notifications => Notifications,
        OptionalFeature.Themes => Themes,
        _ => false,
    };

    public void ApplyTo(AppSettings settings)
    {
        settings.ConsentAutomaticUpdates = AutomaticUpdates;
        settings.ConsentNotifications = Notifications;
        settings.ConsentThemes = Themes;
    }
}
