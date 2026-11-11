using Bydlo.Core.Diagnostics;

namespace Bydlo.App.Services;

/// <summary>
/// Manual update instructions for copies the built-in updater does not own (issue #413).
/// A package-manager install is updated by that manager; a portable copy is replaced by hand.
/// </summary>
public static class UpdateGuidance
{
    public static string KeyFor(AppInstallKind kind) => kind switch
    {
        AppInstallKind.Debian => "settings.update.manual.deb",
        AppInstallKind.Arch => "settings.update.manual.arch",
        AppInstallKind.Nix => "settings.update.manual.nix",
        AppInstallKind.Portable => "settings.update.manual.portable",
        _ => "settings.update.manual.unknown",
    };
}
