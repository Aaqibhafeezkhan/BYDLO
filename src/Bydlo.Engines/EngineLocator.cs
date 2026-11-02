using Bydlo.Core.Models;
using Bydlo.Engines.Installation;

namespace Bydlo.Engines;

/// <summary>
/// Finds engine executables: explicit path from settings → bundled copy → BYDLO-managed install → PATH → well-known install folders
/// (resolved from environment variables, never hardcoded drives).
/// </summary>
public class EngineLocator(EngineInstaller installer, PlatformInfo platform, string? bundleDirectory = null)
{
    public PlatformInfo Platform => platform;

    public EngineInstaller Installer => installer;

    public string? BundledExecutable(string id) => installer.BundledExecutable(id, platform, bundleDirectory);

    public virtual string? Locate(EngineKind kind, AppSettings settings)
    {
        if (settings.EnginePaths.TryGetValue(kind.ToString(), out var custom) && File.Exists(custom))
        {
            return custom;
        }

        var lockId = LockId(kind);
        if (lockId is not null && BundledExecutable(lockId) is { } bundled)
        {
            return bundled;
        }

        if (lockId is not null && installer.InstalledExecutable(lockId, platform) is { } managed)
        {
            return managed;
        }

        foreach (var name in ExecutableNames(kind))
        {
            if (FindOnPath(name) is { } onPath)
            {
                return onPath;
            }

            foreach (var dir in WellKnownDirectories(kind))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static string? LockId(EngineKind kind) => kind switch
    {
        EngineKind.SingBox => "sing-box",
        EngineKind.Xray => "xray",
        EngineKind.Zapret => "zapret",
        EngineKind.AmneziaWG => "amneziawg",
        EngineKind.OpenVpn => "openvpn",
        _ => null,
    };

    private IEnumerable<string> ExecutableNames(EngineKind kind)
    {
        var win = platform.Os == OsFamily.Windows;
        return kind switch
        {
            EngineKind.SingBox => [win ? "sing-box.exe" : "sing-box"],
            EngineKind.Xray => [win ? "xray.exe" : "xray"],
            EngineKind.WireGuard => win ? ["wireguard.exe"] : ["wg-quick"],
            EngineKind.AmneziaWG => [win ? "bydlo-amneziawg.exe" : "bydlo-amneziawg"],
            EngineKind.OpenVpn => [win ? "openvpn.exe" : "openvpn"],
            _ => [],
        };
    }

    private IEnumerable<string> WellKnownDirectories(EngineKind kind)
    {
        if (platform.Os == OsFamily.Windows)
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (string.IsNullOrEmpty(pf))
            {
                yield break;
            }

            switch (kind)
            {
                case EngineKind.WireGuard:
                    yield return Path.Combine(pf, "WireGuard");
                    break;
                case EngineKind.AmneziaWG:
                    yield return Path.Combine(pf, "AmneziaWG");
                    break;
                case EngineKind.OpenVpn:
                    yield return Path.Combine(pf, "OpenVPN", "bin");
                    break;
            }
        }
        else
        {
            yield return "/usr/local/bin";
            yield return "/opt/homebrew/bin";
            yield return "/usr/bin";
            yield return "/usr/sbin";
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
