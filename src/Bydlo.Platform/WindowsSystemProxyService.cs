using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Microsoft.Win32;

namespace Bydlo.Platform;

/// <summary>
/// Per-user WinINet proxy (HKCU Internet Settings). Off by default, only used while connected when the user
/// enables "Set system proxy", and refused under the development safety lock. The previous values are restored on clear.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemProxyService : ISystemProxyService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    private (int Enable, string? Server, string? Override)? _previous;

    public bool IsSupported => true;

    public Task SetAsync(string host, int port, CancellationToken ct = default)
    {
        NetworkSafetyGuard.EnsureAllowed("change the system proxy");
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true) ?? throw new InvalidOperationException("Internet Settings key not found.");
        _previous ??= ((key.GetValue("ProxyEnable") as int?) ?? 0, key.GetValue("ProxyServer") as string, key.GetValue("ProxyOverride") as string);
        key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;192.168.*;<local>", RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        Notify();
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        if (_previous is not { } prev)
        {
            return Task.CompletedTask;
        }

        NetworkSafetyGuard.EnsureAllowed("restore the system proxy");
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is not null)
        {
            key.SetValue("ProxyEnable", prev.Enable, RegistryValueKind.DWord);
            Restore(key, "ProxyServer", prev.Server);
            Restore(key, "ProxyOverride", prev.Override);
        }

        _previous = null;
        Notify();
        return Task.CompletedTask;
    }

    private static void Restore(RegistryKey key, string name, string? value)
    {
        if (value is null)
        {
            key.DeleteValue(name, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}
