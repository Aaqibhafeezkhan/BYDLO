using System.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Installation;

namespace Bydlo.Engines.Zapret;

/// <summary>Common lifecycle for WinDivert, NFQUEUE and PF/tpws backends.</summary>
public sealed class ZapretEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator,
    IPrivilegeService privileges, IZapretFirewall? firewall = null) : ProcessEngineBase(runner, redactor)
{
    public static readonly string[] ConflictingProcesses = ["winws", "goodbyedpi"];

    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.Zapret,
        DisplayName = "Zapret (Flowseal / bol-van)",
        Upstream = "https://github.com/bol-van/zapret",
        License = "MIT; WinDivert: LGPL-3.0 / GPL-2.0",
        IsTool = true,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Unsupported,
        },
    };

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => false;

    /// <summary>Bundled pinned copy, then managed pinned copy, then user folder.</summary>
    public ZapretInstallation? ResolveInstallation(AppSettings settings)
    {
        var os = locator.Platform.Os;
        var id = os == OsFamily.Windows ? "zapret" : "zapret-native";
        var lists = Path.Combine(locator.Installer.UserDataDirectory, "lists");
        foreach (var exe in new[] { locator.BundledExecutable(id), locator.Installer.InstalledExecutable(id, locator.Platform) })
        {
            if (exe is null) { continue; }
            var candidate = os == OsFamily.Windows
                ? new ZapretInstallation(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, lists)
                : new ZapretInstallation(Path.GetDirectoryName(exe)!, lists, exe, os) { ReleaseVersion = locator.Installer.PinnedVersion(id) };
            if (candidate.IsValid) { return Migrate(candidate, settings); }
        }

        if (!string.IsNullOrWhiteSpace(settings.Zapret.InstallPath))
        {
            var root = settings.Zapret.InstallPath;
            var candidate = os == OsFamily.Windows ? new ZapretInstallation(root, lists)
                : new ZapretInstallation(root, lists, Path.Combine(root, os == OsFamily.MacOS ? "tpws" : "nfqws"), os);
            if (candidate.IsValid) { return Migrate(candidate, settings); }
        }

        return null;
    }

    private static ZapretInstallation Migrate(ZapretInstallation install, AppSettings settings)
    {
        if (install.Os == OsFamily.Windows)
        {
            // The former explicit folder's lists take priority during one-time migration.
            if (!string.IsNullOrWhiteSpace(settings.Zapret.InstallPath) && Directory.Exists(settings.Zapret.InstallPath))
            {
                install.ImportUserListsFrom(new ZapretInstallation(settings.Zapret.InstallPath));
            }
            install.ImportUserListsFrom(new ZapretInstallation(install.Root));
        }

        return install;
    }

    public static string? DefaultStrategy(ZapretInstallation install) =>
        install.Strategies.FirstOrDefault(s => s.FileName == "general.bat")?.FileName ??
        (install.Strategies.Count > 0 ? install.Strategies[0].FileName : null);

    private static string? Strategy(AppSettings settings, ZapretInstallation install) =>
        install.Strategies.Any(s => s.FileName == settings.Zapret.Strategy)
            ? settings.Zapret.Strategy : DefaultStrategy(install);

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        var os = locator.Platform.Os;
        if (os == OsFamily.Android)
        {
            return Task.FromResult(EngineAvailability.Unsupported("Android Zapret requires a separate root / VpnService implementation."));
        }

        if (os == OsFamily.Unknown || locator.Platform.Arch == CpuArch.Other || (os == OsFamily.Windows && locator.Platform.Arch != CpuArch.X64))
        {
            return Task.FromResult(EngineAvailability.NotImplemented("No compatible pinned Zapret build / WinDivert driver for this architecture."));
        }

        if (os != OsFamily.Windows && firewall is null)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Native Zapret needs the privileged firewall service."));
        }

        var install = ResolveInstallation(settings);
        if (install is null || !install.IsValid)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled,
                "Zapret is missing or quarantined. Restore the official BYDLO release or download the pinned engine in Tools. See docs/TESTING.md."));
        }

        if (Strategy(settings, install) is null)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "No supported Zapret strategies were found.", install.ExecutablePath));
        }

        return Task.FromResult(!privileges.IsElevated
            ? new EngineAvailability(AvailabilityStatus.NeedsElevation, "Zapret needs administrator / root rights.", install.ExecutablePath)
            : EngineAvailability.Ready(install.ExecutablePath));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var install = ResolveInstallation(request.Settings)!;
        var names = locator.Platform.Os == OsFamily.Windows ? ConflictingProcesses : ["nfqws", "tpws"];
        var running = names.SelectMany(SafeGetProcesses).Distinct().ToArray();
        if (running.Length > 0)
        {
            throw new EngineException($"Another DPI bypass is already running ({string.Join(", ", running)}). Stop it before starting Zapret.");
        }

        install.EnsureUserLists();
        IZapretBackend backend = locator.Platform.Os switch
        {
            OsFamily.Windows => new WinDivertBackend(),
            OsFamily.Linux => new NfqueueBackend(),
            OsFamily.MacOS => new PfTpwsBackend(),
            _ => throw new PlatformNotSupportedException(),
        };
        var parsed = await backend.PrepareAsync(install, Strategy(request.Settings, install)!, request.Settings.Zapret, ct).ConfigureAwait(false);
        foreach (var warning in parsed.Warnings) { EmitLog("warning: " + warning); }

        return new ProcessSpec
        {
            FileName = install.ExecutablePath,
            Arguments = parsed.Arguments,
            WorkingDirectory = Path.GetDirectoryName(install.ExecutablePath),
            DisplayName = locator.Platform.Os == OsFamily.Windows ? "winws" : locator.Platform.Os == OsFamily.Linux ? "nfqws" : "tpws",
        };
    }

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
    {
        if (locator.Platform.Os == OsFamily.Windows) { return Task.CompletedTask; }
        var install = ResolveInstallation(request.Settings)!;
        var excluded = File.ReadLines(install.UserListPath(ZapretUserList.IpsetExclude))
            .Select(l => l.Split('#')[0].Trim()).Where(l => l.Length > 0).ToArray();
        return firewall!.ApplyAsync(locator.Platform.Os, NativeZapretStrategy.TcpPorts(request.Settings.Zapret.GameFilter),
            NativeZapretStrategy.UdpPorts(request.Settings.Zapret.GameFilter), excluded, ct);
    }

    protected override Task OnStoppingAsync() => locator.Platform.Os == OsFamily.Windows ? Task.CompletedTask : firewall?.RemoveAsync() ?? Task.CompletedTask;

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(5);

    protected override bool IsReadyLine(string line) => line.Contains("capture is started", StringComparison.OrdinalIgnoreCase);

    protected override string? TryParseFatal(string line) =>
        line.Contains("windivert", StringComparison.OrdinalIgnoreCase) && line.Contains("error", StringComparison.OrdinalIgnoreCase) ? line
        : line.StartsWith("could not", StringComparison.OrdinalIgnoreCase) ? line : null;

    private static IEnumerable<string> SafeGetProcesses(string name)
    {
        Process[] found;
        try { found = Process.GetProcessesByName(name); }
        catch (InvalidOperationException) { yield break; }
        foreach (var p in found) { p.Dispose(); yield return name; }
    }
}

/// <summary>Updates the managed engine without overwriting data-directory user lists.</summary>
public sealed class ZapretUpdater(EngineInstaller installer, PlatformInfo platform)
{
    public async Task<ZapretInstallation> InstallOrUpdateAsync(ZapretInstallation? previous, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var lists = Path.Combine(installer.UserDataDirectory, "lists");
        if (previous is { IsValid: true } && !string.Equals(previous.UserListsDirectory, lists, StringComparison.OrdinalIgnoreCase))
        {
            new ZapretInstallation(previous.Root, lists).ImportUserListsFrom(previous);
        }
        var exe = await installer.InstallAsync(platform.Os == OsFamily.Windows ? "zapret" : "zapret-native", platform, progress, ct).ConfigureAwait(false);
        var install = platform.Os == OsFamily.Windows
            ? new ZapretInstallation(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!, lists)
            : new ZapretInstallation(Path.GetDirectoryName(exe)!, lists, exe, platform.Os) { ReleaseVersion = installer.PinnedVersion("zapret-native") };
        if (previous is { IsValid: true } && !string.Equals(previous.UserListsDirectory, lists, StringComparison.OrdinalIgnoreCase))
        {
            install.ImportUserListsFrom(previous);
        }

        install.EnsureUserLists();
        return install;
    }
}
