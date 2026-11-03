using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue #246: opt-in elevation at startup, limited mode on decline.</summary>
public class StartupElevationTests
{
    private sealed class Priv(bool elevated, bool canRestart) : IPrivilegeService
    {
        public bool IsElevated => elevated;
        public bool CanRestartElevated => canRestart;
        public bool TryRestartElevated(IReadOnlyList<string> args) => true;
    }

    [Fact]
    public void Default_is_off()
    {
        Assert.False(new AppSettings().RunElevatedOnStartup);
    }

    [Fact]
    public void Elevates_only_when_opted_in_and_capable()
    {
        var s = new AppSettings { RunElevatedOnStartup = true };
        Assert.True(StartupElevation.ShouldElevateOnStartup(s, new Priv(false, true), isMock: false, isLocked: false));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void No_elevation_in_mock_or_safety_lock_or_without_opt_in(bool optIn, bool mock, bool locked)
    {
        var s = new AppSettings { RunElevatedOnStartup = optIn };
        Assert.False(StartupElevation.ShouldElevateOnStartup(s, new Priv(false, true), mock, locked));
    }

    [Fact]
    public void No_elevation_when_already_elevated_or_unsupported()
    {
        var s = new AppSettings { RunElevatedOnStartup = true };
        Assert.False(StartupElevation.ShouldElevateOnStartup(s, new Priv(true, true), false, false));
        Assert.False(StartupElevation.ShouldElevateOnStartup(s, new Priv(false, false), false, false));
    }

    [Fact]
    public void Safety_lock_blocks_privileged_operations()
    {
        Assert.True(NetworkSafetyGuard.IsLocked);
        var s = new AppSettings { RunElevatedOnStartup = true };
        Assert.False(StartupElevation.ShouldElevateOnStartup(s, new Priv(false, true), false, true));
        Assert.False(StartupElevation.ShouldElevateOnStartup(s, new Priv(false, true), false, NetworkSafetyGuard.IsLocked));
    }

    [Fact]
    public void Strings_and_settings_ui_exist_in_both_languages()
    {
        var root = RepositoryRoot();
        var en = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "en.json"));
        var ru = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Assets", "Strings", "ru.json"));
        Assert.Contains("settings.admin.autorestart", en, StringComparison.Ordinal);
        Assert.Contains("settings.admin.autorestart.hint", en, StringComparison.Ordinal);
        Assert.Contains("settings.admin.autorestart", ru, StringComparison.Ordinal);
        var view = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "Views", "AdvancedSettingsView.axaml"));
        Assert.Contains("RunElevatedOnStartup", view, StringComparison.Ordinal);
        Assert.Contains("settings.admin.autorestart", view, StringComparison.Ordinal);
        var vm = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "ViewModels", "SettingsViewModel.cs"));
        Assert.Contains("RunElevatedOnStartup", vm, StringComparison.Ordinal);
    }

    [Fact]
    public void Decline_keeps_limited_mode_and_filters_parent_argument()
    {
        var root = RepositoryRoot();
        var app = File.ReadAllText(Path.Combine(root, "src", "Bydlo.App", "App.axaml.cs"));
        // Startup elevation filters the handoff marker so it does not accumulate across restarts.
        Assert.Contains("ParentArgument", app, StringComparison.Ordinal);
        Assert.Contains("ShouldElevateOnStartup", app, StringComparison.Ordinal);
        // On decline/failure there is no Exit: the app continues with limited features.
        Assert.Contains("TryRestartElevated", app, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0)", app, StringComparison.Ordinal);
        // The Exit above is inside the success branch; the fallthrough continues startup.
        var exitIndex = app.IndexOf("Environment.Exit(0)", StringComparison.Ordinal);
        var tail = app.Substring(exitIndex);
        Assert.Contains("return;", tail, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BYDLO.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
