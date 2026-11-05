using Bydlo.Platform;

namespace Bydlo.Tests;

public sealed class LauncherPathTests
{
    private const string Process = "/opt/bydlo/lib/BYDLO";

    private static string? Resolve(bool linux, Dictionary<string, string> env, params string[] existing) =>
        LauncherPath.Resolve(linux, name => env.GetValueOrDefault(name), Process, existing.Contains);

    [Fact]
    public void Falls_back_to_process_path_without_overrides() =>
        Assert.Equal(Process, Resolve(true, []));

    [Fact]
    public void Wrapper_launcher_wins_over_appimage()
    {
        var env = new Dictionary<string, string> { ["BYDLO_LAUNCHER"] = "/nix/bin/bydlo", ["APPIMAGE"] = "/home/u/BYDLO.AppImage" };
        Assert.Equal("/nix/bin/bydlo", Resolve(true, env, "/nix/bin/bydlo", "/home/u/BYDLO.AppImage"));
    }

    [Fact]
    public void Uses_appimage_file_instead_of_transient_mount()
    {
        var env = new Dictionary<string, string> { ["APPIMAGE"] = "/home/u/BYDLO.AppImage" };
        Assert.Equal("/home/u/BYDLO.AppImage", Resolve(true, env, "/home/u/BYDLO.AppImage"));
    }

    [Theory]
    [InlineData("relative/bydlo")]
    [InlineData("/missing/bydlo")]
    [InlineData("/bad\npath")]
    public void Ignores_invalid_overrides(string value)
    {
        var env = new Dictionary<string, string> { ["BYDLO_LAUNCHER"] = value };
        Assert.Equal(Process, Resolve(true, env, "relative/bydlo", "/bad\npath"));
    }

    [Fact]
    public void Ignores_overrides_outside_linux()
    {
        var env = new Dictionary<string, string> { ["BYDLO_LAUNCHER"] = "/nix/bin/bydlo" };
        Assert.Equal(Process, Resolve(false, env, "/nix/bin/bydlo"));
    }
}
