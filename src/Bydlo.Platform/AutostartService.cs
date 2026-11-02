using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace Bydlo.Platform;

/// <summary>Opt-in, per-user login registration. Never requires administrator privileges.</summary>
public sealed class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string? _executable = Environment.ProcessPath;

    public bool IsSupported => (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        _executable is not null && !Path.GetFileNameWithoutExtension(_executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported) { throw new PlatformNotSupportedException(); }
        if (OperatingSystem.IsWindows())
        {
            SetWindows(enabled);
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path;
        string content;
        if (OperatingSystem.IsMacOS())
        {
            path = Path.Combine(home, "Library", "LaunchAgents", "org.bydlo.desktop.plist");
            content = MacRegistration(_executable!);
        }
        else
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config)) { config = Path.Combine(home, ".config"); }
            path = Path.Combine(config, "autostart", "bydlo.desktop");
            content = LinuxRegistration(_executable!);
        }

        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        else { File.Delete(path); }
    }

    [SupportedOSPlatform("windows")]
    private void SetWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) { key.SetValue("BYDLO", $"\"{_executable}\" --autostart"); }
        else { key.DeleteValue("BYDLO", throwOnMissingValue: false); }
    }

    internal static string LinuxRegistration(string executable)
    {
        // Exec quoting and desktop-entry string escaping are separate layers.
        var quoted = executable.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
        var exec = ("\"" + quoted + "\" --autostart").Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal);
        return $"[Desktop Entry]\nType=Application\nName=BYDLO\nExec={exec}\nTerminal=false\n";
    }

    internal static string MacRegistration(string executable) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\"><dict><key>Label</key><string>org.bydlo.desktop</string>" +
        $"<key>ProgramArguments</key><array><string>{SecurityElement.Escape(executable)}</string><string>--autostart</string></array>" +
        "<key>RunAtLoad</key><true/></dict></plist>\n";
}
