namespace Bydlo.Core.Diagnostics;

/// <summary>
/// Single user-visible update state shared by the release check and the packaged updater (issue #413).
/// The release notification may only move the state forward, so a downloaded package is never hidden.
/// </summary>
public enum AppUpdateState
{
    /// <summary>Nothing known yet.</summary>
    Idle,

    /// <summary>A newer release exists, but no package is downloaded.</summary>
    Available,

    /// <summary>The package is downloading.</summary>
    Downloading,

    /// <summary>The package is downloaded and installs on the next real exit.</summary>
    Ready,

    /// <summary>The package is downloaded, but installation is postponed.</summary>
    Deferred,

    /// <summary>The last download or installation attempt failed.</summary>
    Failed,
}

/// <summary>Outcome of handing a downloaded package to the installer at exit.</summary>
public enum AppUpdateApplyResult
{
    /// <summary>Nothing was downloaded, the opt-in is off, or the session is blocked.</summary>
    Nothing,

    /// <summary>The installer takes over once this process exits.</summary>
    Applied,

    /// <summary>The package stays downloaded; installation waits for the next exit.</summary>
    Deferred,

    /// <summary>The installer refused the package.</summary>
    Failed,
}
