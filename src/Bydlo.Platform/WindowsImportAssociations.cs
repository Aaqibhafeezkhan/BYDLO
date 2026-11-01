using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Bydlo.Platform;

/// <summary>Opt-in per-user registrations. Never changes file defaults or requires elevation.</summary>
public static class WindowsImportAssociations
{
    public static IReadOnlyList<string> Protocols { get; } = Array.AsReadOnly(new[] { "vless", "vmess", "trojan", "ss", "hy2", "hysteria2", "tuic" });

    public static string OpenCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (executable.Contains('"') || !Path.IsPathFullyQualified(executable))
        {
            throw new ArgumentException("An absolute executable path is required.", nameof(executable));
        }

        return $"\"{executable}\" \"%1\"";
    }

    [SupportedOSPlatform("windows")]
    public static void Register(string executable)
    {
        var command = OpenCommand(executable);
        // Do not silently replace another application's custom protocol handler.
        foreach (var protocol in Protocols)
        {
            using var existing = Registry.ClassesRoot.OpenSubKey($@"{protocol}\shell\open\command");
            if (existing?.GetValue(null) is string other && other != command)
            {
                throw new InvalidOperationException("Another application already handles this protocol.");
            }
        }

        Set(@"Software\Classes\BYDLO.Config", "", "BYDLO configuration");
        Set(@"Software\Classes\BYDLO.Config\shell\open\command", "", command);
        foreach (var extension in new[] { ".conf", ".ovpn" })
        {
            Set($@"Software\Classes\{extension}\OpenWithProgids", "BYDLO.Config", "");
        }

        foreach (var protocol in Protocols)
        {
            Set($@"Software\Classes\{protocol}", "", $"URL:BYDLO {protocol}");
            Set($@"Software\Classes\{protocol}", "URL Protocol", "");
            Set($@"Software\Classes\{protocol}\shell\open\command", "", command);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Unregister(string executable)
    {
        var command = OpenCommand(executable);
        foreach (var protocol in Protocols.Append("BYDLO.Config"))
        {
            var path = $@"Software\Classes\{protocol}";
            using var existing = Registry.CurrentUser.OpenSubKey(path + @"\shell\open\command");
            if (existing?.GetValue(null) as string == command)
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        }

        foreach (var extension in new[] { ".conf", ".ovpn" })
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{extension}\OpenWithProgids", writable: true);
            key?.DeleteValue("BYDLO.Config", throwOnMissingValue: false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Set(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value);
    }
}
