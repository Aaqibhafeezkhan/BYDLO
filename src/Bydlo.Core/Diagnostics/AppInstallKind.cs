namespace Bydlo.Core.Diagnostics;

/// <summary>How this copy of BYDLO was installed. Decides who owns updates (issue #413).</summary>
public enum AppInstallKind
{
    /// <summary>Unpackaged build or an unrecognised layout: no automatic updates.</summary>
    Unknown,

    /// <summary>Installed by a Velopack setup package (Setup.exe, AppImage, Setup.pkg).</summary>
    Velopack,

    /// <summary>Portable folder or an old ZIP release.</summary>
    Portable,

    /// <summary>Installed from the <c>.deb</c> package.</summary>
    Debian,

    /// <summary>Installed from an Arch package.</summary>
    Arch,

    /// <summary>Installed from the Nix flake.</summary>
    Nix,
}

/// <summary>Who is responsible for updating this copy.</summary>
public enum AppUpdateOwner
{
    /// <summary>Nobody: the layout is unknown, so the UI must not promise updates.</summary>
    None,

    /// <summary>The built-in Velopack updater, after an explicit opt-in.</summary>
    Velopack,

    /// <summary>apt, pacman or Nix; the built-in updater never writes to those files.</summary>
    PackageManager,

    /// <summary>The user replaces the folder by hand.</summary>
    Manual,
}

/// <summary>Read-only file probes. The locator performs no I/O itself, so tests stay isolated.</summary>
public interface IAppInstallProbe
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    string? ReadText(string path);
}

/// <summary>Real file system probe used by the application.</summary>
public sealed class FileSystemInstallProbe : IAppInstallProbe
{
    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Detects the install kind from package markers instead of guessing from a writable path (issue #413).
/// Every marker is a file or directory the installer itself creates, so a copy that merely happens to be
/// writable is never mistaken for a managed installation.
/// </summary>
public sealed class AppInstallLocator(IAppInstallProbe probe)
{
    /// <summary>Marker written by distribution packages into the application directory.</summary>
    public const string MarkerFileName = "install-kind";

    /// <summary>Velopack writes this file next to the application on every install and update.</summary>
    public const string VelopackMarkerFileName = "sq.version";

    /// <summary>Portable layout marker used by earlier releases.</summary>
    public const string PortableMarkerFileName = "portable.txt";

    /// <summary>Release bundles ship the pinned engines and their checksums next to the executable.</summary>
    public const string BundleChecksumsFileName = "ENGINES-SHA256SUMS.txt";

    /// <summary>Directory of the pinned engines inside a release bundle.</summary>
    public const string BundleEnginesDirectoryName = "engines";

    private const string DebianDatabaseFile = "/var/lib/dpkg/info/bydlo.list";
    private const string PacmanDatabaseDirectory = "/var/lib/pacman/local";
    private const string DebianApplicationDirectory = "/opt/bydlo";
    private const string NixStorePrefix = "/nix/store/";

    public static AppInstallLocator CreateDefault() => new(new FileSystemInstallProbe());

    /// <summary>
    /// Classifies a copy. <paramref name="executablePath"/> is optional and only used for the Nix store check,
    /// where the launcher may live outside the application directory.
    /// </summary>
    public AppInstallKind Detect(string applicationDirectory, string? executablePath = null)
    {
        if (Marker(applicationDirectory) is { } marked)
        {
            return marked;
        }

        if (probe.FileExists(Path.Combine(applicationDirectory, PortableMarkerFileName)))
        {
            return AppInstallKind.Portable;
        }

        if (IsVelopack(applicationDirectory))
        {
            return AppInstallKind.Velopack;
        }

        if (IsNixStore(applicationDirectory) || (executablePath is not null && IsNixStore(executablePath)))
        {
            return AppInstallKind.Nix;
        }

        // The dpkg record alone is not enough: a portable copy on the same machine is still manual.
        if (IsDebianPackage(applicationDirectory))
        {
            return AppInstallKind.Debian;
        }

        if (IsArchPackage(applicationDirectory))
        {
            return AppInstallKind.Arch;
        }

        return IsReleaseBundle(applicationDirectory) ? AppInstallKind.Portable : AppInstallKind.Unknown;
    }

    private AppInstallKind? Marker(string applicationDirectory) =>
        probe.ReadText(Path.Combine(applicationDirectory, MarkerFileName))?.Trim().ToLowerInvariant() switch
        {
            "velopack" => AppInstallKind.Velopack,
            "portable" => AppInstallKind.Portable,
            "deb" or "debian" => AppInstallKind.Debian,
            "arch" => AppInstallKind.Arch,
            "nix" => AppInstallKind.Nix,
            _ => null,
        };

    private bool IsVelopack(string applicationDirectory)
    {
        if (probe.FileExists(Path.Combine(applicationDirectory, VelopackMarkerFileName)))
        {
            return true;
        }

        // Windows installs keep Update.exe one level above the versioned "current" directory.
        return Path.GetDirectoryName(applicationDirectory) is { Length: > 0 } parent &&
            probe.FileExists(Path.Combine(parent, "Update.exe"));
    }

    private bool IsDebianPackage(string applicationDirectory) =>
        Normalize(applicationDirectory).Equals(DebianApplicationDirectory, StringComparison.Ordinal) &&
        probe.FileExists(DebianDatabaseFile);

    private bool IsArchPackage(string applicationDirectory) =>
        Normalize(applicationDirectory).Equals(DebianApplicationDirectory, StringComparison.Ordinal) &&
        probe.DirectoryExists(PacmanDatabaseDirectory);

    private bool IsReleaseBundle(string applicationDirectory) =>
        probe.DirectoryExists(Path.Combine(applicationDirectory, BundleEnginesDirectoryName)) &&
        probe.FileExists(Path.Combine(applicationDirectory, BundleChecksumsFileName));

    private static bool IsNixStore(string path) =>
        Normalize(path).StartsWith(NixStorePrefix, StringComparison.Ordinal);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>Maps an install kind to the component that owns its updates.</summary>
public static class AppUpdateOwnership
{
    public static AppUpdateOwner OwnerOf(AppInstallKind kind) => kind switch
    {
        AppInstallKind.Velopack => AppUpdateOwner.Velopack,
        AppInstallKind.Debian or AppInstallKind.Arch or AppInstallKind.Nix => AppUpdateOwner.PackageManager,
        AppInstallKind.Portable => AppUpdateOwner.Manual,
        _ => AppUpdateOwner.None,
    };
}
