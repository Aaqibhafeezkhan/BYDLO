using System.Security;
using Bydlo.Core.Models;

namespace Bydlo.Platform;

/// <summary>Opt-in, per-user login registration. Never requires administrator privileges.</summary>
public sealed class AutostartService(PlatformInfo? platform = null, string? executable = null, IWindowsRegistrationStore? registrations = null)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string? _executable = executable ?? LauncherPath.Current;
    private readonly PlatformInfo _platform = platform ?? PlatformInfo.Current;

    public bool IsSupported => (_platform.Os is OsFamily.Windows or OsFamily.Linux or OsFamily.MacOS) &&
        _executable is not null && !Path.GetFileNameWithoutExtension(new Bydlo.Core.Platform.TargetPathRules(_platform.Os).FileName(_executable))
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    public void SetEnabled(bool enabled)
    {
        if (!IsSupported) { throw new PlatformNotSupportedException(); }
        if (_platform.Os == OsFamily.Windows)
        {
            SetWindows(enabled);
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string path;
        string content;
        if (_platform.Os == OsFamily.MacOS)
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

    private void SetWindows(bool enabled)
    {
        var store = registrations ?? CreateNativeRegistrations();
        if (enabled) { store.Set(RunKey, "BYDLO", $"\"{_executable}\" --autostart"); }
        else { store.DeleteValue(RunKey, "BYDLO"); }
    }

    private static WindowsRegistrationStore CreateNativeRegistrations() => OperatingSystem.IsWindows()
        ? new WindowsRegistrationStore() : throw new PlatformNotSupportedException();

    internal static string LinuxRegistration(string executable, string arguments = "--autostart")
    {
        // Exec quoting and desktop-entry string escaping are separate layers.
        var quoted = executable.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal);
        var exec = ("\"" + quoted + "\" " + arguments).Replace("\\", "\\\\", StringComparison.Ordinal)
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
