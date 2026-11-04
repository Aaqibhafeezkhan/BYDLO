using Bydlo.Helper;

namespace Bydlo.Tests;

/// <summary>
/// Issue #221: helper service definitions and path guards are pure and pinned
/// without root, systemctl/launchctl/sc.exe or any real process.
/// </summary>
public class HelperServiceDefinitionTests
{
    [Fact]
    public void Systemd_unit_runs_helper_as_root_service()
    {
        var unit = HelperServiceDefinitions.BuildSystemdUnit("/opt/bydlo/bin/Bydlo.Helper");
        Assert.Contains("ExecStart=\"/opt/bydlo/bin/Bydlo.Helper\" service", unit, StringComparison.Ordinal);
        Assert.Contains("User=root", unit, StringComparison.Ordinal);
        Assert.Contains("WantedBy=multi-user.target", unit, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", unit, StringComparison.Ordinal);
        Assert.Contains("TimeoutStopSec=45", unit, StringComparison.Ordinal);
    }

    [Fact]
    public void Launchd_plist_escapes_executable_and_runs_at_load()
    {
        var plist = HelperServiceDefinitions.BuildLaunchDaemon("/opt/a&b/Bydlo.Helper");
        Assert.Contains("<string>online.bydlo.helper</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<string>/opt/a&amp;b/Bydlo.Helper</string><string>service</string>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>RunAtLoad</key><true/>", plist, StringComparison.Ordinal);
        Assert.Contains("<key>UserName</key><string>root</string>", plist, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_service_args_keep_install_order()
    {
        Assert.Equal(
            ["create", "BYDLOHelper", "binPath=", "\"C:\\H\\Bydlo.Helper.exe\" service", "start=", "auto", "obj=", "LocalSystem"],
            HelperServiceDefinitions.BuildServiceCreateArgs("C:\\H\\Bydlo.Helper.exe", "BYDLOHelper"));
        Assert.Equal(
            ["description", "BYDLOHelper", "BYDLO privileged engine helper (one explicitly authorized user)"],
            HelperServiceDefinitions.BuildServiceDescriptionArgs("BYDLOHelper"));
        Assert.Equal(["start", "BYDLOHelper"], HelperServiceDefinitions.BuildServiceStartArgs("BYDLOHelper"));
        Assert.Equal(["delete", "BYDLOHelper"], HelperServiceDefinitions.BuildServiceDeleteArgs("BYDLOHelper"));
    }

    public static TheoryData<string, string, string, bool> DeleteGuardCases()
    {
        var root = Path.Combine("base", "lib", "bydlo-helper");
        return new TheoryData<string, string, string, bool>
        {
            { root, root, Path.Combine("opt", "bydlo"), true },
            { Path.Combine("base", "other"), root, Path.Combine("opt", "bydlo"), false },
            { root, root, Path.Combine(root, "bin"), false },
        };
    }

    [Theory]
    [MemberData(nameof(DeleteGuardCases))]
    public void Delete_guard_allows_only_expected_root_outside_it(string root, string expected, string baseDir, bool safe)
    {
        Assert.Equal(safe, HelperServiceDefinitions.IsSafeToDeleteRoot(root, expected, baseDir, StringComparison.Ordinal));
    }

    [Fact]
    public void Delete_guard_is_case_insensitive_on_windows()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True(HelperServiceDefinitions.IsSafeToDeleteRoot(
            "C:\\Program Files\\BYDLO Helper", "c:\\program files\\bydlo helper", "C:\\Release", StringComparison.OrdinalIgnoreCase));
        Assert.False(HelperServiceDefinitions.IsSafeToDeleteRoot(
            "C:\\Program Files\\BYDLO Helper", "c:\\program files\\bydlo helper", "C:\\Program Files\\BYDLO Helper\\app", StringComparison.OrdinalIgnoreCase));
    }
}
