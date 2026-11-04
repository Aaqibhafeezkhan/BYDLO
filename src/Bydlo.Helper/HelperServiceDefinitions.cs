namespace Bydlo.Helper;

/// <summary>
/// Pure builders for privileged helper service definitions (issue #221).
/// No filesystem, process or privilege calls: unit tests pin the systemd unit,
/// launchd plist and sc.exe arguments without root and without systemctl/launchctl/sc.
/// </summary>
internal static class HelperServiceDefinitions
{
    internal const string Label = "online.bydlo.helper";
    internal const string ServiceDescription = "BYDLO privileged engine helper (one explicitly authorized user)";

    internal static string BuildSystemdUnit(string executable) => $"""
        [Unit]
        Description=BYDLO privileged engine helper
        After=network.target
        [Service]
        Type=exec
        ExecStart="{executable}" service
        User=root
        UMask=0077
        Restart=on-failure
        KillMode=mixed
        TimeoutStopSec=45
        [Install]
        WantedBy=multi-user.target
        """;

    internal static string BuildLaunchDaemon(string executable, string label = Label) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
        <key>Label</key><string>{label}</string>
        <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(executable)}</string><string>service</string></array>
        <key>RunAtLoad</key><true/>
        <key>KeepAlive</key><true/>
        <key>UserName</key><string>root</string>
        <key>Umask</key><integer>63</integer>
        <key>ExitTimeOut</key><integer>45</integer>
        </dict></plist>
        """;

    internal static string[] BuildServiceCreateArgs(string executable, string serviceName) =>
        ["create", serviceName, "binPath=", $"\"{executable}\" service", "start=", "auto", "obj=", "LocalSystem"];

    internal static string[] BuildServiceDescriptionArgs(string serviceName) =>
        ["description", serviceName, ServiceDescription];

    internal static string[] BuildServiceStartArgs(string serviceName) => ["start", serviceName];

    internal static string[] BuildServiceDeleteArgs(string serviceName) => ["delete", serviceName];

    /// <summary>
    /// Guards <c>Remove</c>: only the fixed installation may be deleted, and never while
    /// this executable runs from inside it. Pure string logic, no filesystem access.
    /// </summary>
    internal static bool IsSafeToDeleteRoot(string root, string expected, string baseDirectory, StringComparison comparison) =>
        root.Equals(expected, comparison) && !baseDirectory.StartsWith(root + Path.DirectorySeparatorChar, comparison);
}
