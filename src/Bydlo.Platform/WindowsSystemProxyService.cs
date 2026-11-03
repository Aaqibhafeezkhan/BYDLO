using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bydlo.Core.Platform;
using Microsoft.Win32;

namespace Bydlo.Platform;

/// <summary>Per-user WinINet proxy with durable, conditional crash recovery.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSystemProxyService : ISystemProxyService, IDisposable
{
    private readonly WindowsProxyRecovery _recovery;

    public WindowsSystemProxyService() : this(AppPathsFactory.Create().DataDirectory) { }

    public WindowsSystemProxyService(string directory)
    {
        _recovery = new WindowsProxyRecovery(new RegistryProxySettings(), directory);
    }

    public bool IsSupported => true;
    public Task SetAsync(string host, int port, CancellationToken ct = default) => _recovery.SetAsync(host, port, ct);
    public Task ClearAsync(CancellationToken ct = default) => _recovery.ClearAsync(ct);
    public void Dispose() => _recovery.Dispose();

    private sealed class RegistryProxySettings : IWindowsProxySettings
    {
        private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

        public WindowsProxyValue? Read(string name)
        {
            using var key = Open();
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) { return null; }
            return key.GetValueKind(name) switch
            {
                RegistryValueKind.DWord => new(null, (int)value),
                RegistryValueKind.String => new((string)value, null),
                RegistryValueKind.ExpandString => new((string)value, null, true),
                _ => throw new InvalidDataException("Unsupported system proxy registry value type."),
            };
        }

        public void Write(string name, WindowsProxyValue? value)
        {
            using var key = Open();
            if (value is null) { key.DeleteValue(name, throwOnMissingValue: false); }
            else if (value.Number is { } number) { key.SetValue(name, number, RegistryValueKind.DWord); }
            else { key.SetValue(name, value.Text!, value.Expand ? RegistryValueKind.ExpandString : RegistryValueKind.String); }
            key.Flush();
        }

        private static RegistryKey Open() => Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? throw new IOException("Internet Settings key not found.");

        public void Notify()
        {
            if (!InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0) ||
                !InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0))
            {
                throw new IOException("Could not refresh WinINet proxy settings.");
            }
        }
    }

    [LibraryImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int bufferLength);
}
