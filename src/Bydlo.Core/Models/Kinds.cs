using System.Runtime.InteropServices;

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
    AnyTls,
    ShadowTls,
    Naive,
    Ssh,
    SingBoxConfig,
    XrayConfig,
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
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Create("ANDROID")) ? OsFamily.Android
            : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OsFamily.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OsFamily.MacOS
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OsFamily.Linux
            : OsFamily.Unknown;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => CpuArch.X64,
            Architecture.Arm64 => CpuArch.Arm64,
            _ => CpuArch.Other,
        };
        return new PlatformInfo(os, arch);
    }

    /// <summary>Platform id used in engines.lock.json, e.g. <c>windows-x64</c>.</summary>
    public string Rid => $"{Os.ToString().ToLowerInvariant()}-{Arch.ToString().ToLowerInvariant()}";
}
