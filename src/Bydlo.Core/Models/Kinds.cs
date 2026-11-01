namespace Bydlo.Core.Models;

/// <summary>Network technology described by a profile.</summary>
public enum ProtocolKind
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
    Tuic,
    Socks5,
    Http,
    WireGuard,
    AmneziaWG,
    OpenVpn,
}

/// <summary>Executable or library that actually carries traffic.</summary>
public enum EngineKind
{
    Mock,
    SingBox,
    Xray,
    WireGuard,
    AmneziaWG,
    OpenVpn,
    Zapret,
    TgWsProxy,
}

/// <summary>How the primary tunnel is exposed to the system.</summary>
public enum ConnectionMode
{
    /// <summary>Local SOCKS5/HTTP proxy on 127.0.0.1 only. No system changes, no elevation.</summary>
    LocalProxy,

    /// <summary>Full-device TUN interface. Requires elevation and changes routing (only while connected).</summary>
    Tun,
}

public enum OsFamily
{
    Windows,
    Linux,
    MacOS,
    Android,
    Unknown,
}

public enum CpuArch
{
    X64,
    Arm64,
    Other,
}

public sealed record PlatformInfo(OsFamily Os, CpuArch Arch)
{
    public static PlatformInfo Current { get; } = Detect();

    private static PlatformInfo Detect()
    {
        var os = OperatingSystem.IsAndroid() ? OsFamily.Android
            : OperatingSystem.IsWindows() ? OsFamily.Windows
            : OperatingSystem.IsMacOS() ? OsFamily.MacOS
            : OperatingSystem.IsLinux() ? OsFamily.Linux
            : OsFamily.Unknown;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => CpuArch.X64,
            System.Runtime.InteropServices.Architecture.Arm64 => CpuArch.Arm64,
            _ => CpuArch.Other,
        };
        return new PlatformInfo(os, arch);
    }

    /// <summary>Platform id used in engines.lock.json, e.g. <c>windows-x64</c>.</summary>
    public string Rid => $"{Os.ToString().ToLowerInvariant()}-{Arch.ToString().ToLowerInvariant()}";
}
