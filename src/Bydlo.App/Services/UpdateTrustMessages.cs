using Bydlo.Core.Updates;

namespace Bydlo.App.Services;

/// <summary>
/// Localized message for a refused update (issue #411). Every failure keeps the installed version,
/// so the text always says that nothing was installed.
/// </summary>
internal static class UpdateTrustMessages
{
    public static string Key(UpdateTrustFailure failure) => failure switch
    {
        UpdateTrustFailure.Expired or UpdateTrustFailure.NotYetValid => "update.trust.expired",
        UpdateTrustFailure.WrongChannel or UpdateTrustFailure.UnsupportedRid => "update.trust.unsupported",
        UpdateTrustFailure.NotNewer => "update.trust.downgrade",
        UpdateTrustFailure.AssetMismatch => "update.trust.mismatch",
        _ => "update.trust.catalog",
    };
}
