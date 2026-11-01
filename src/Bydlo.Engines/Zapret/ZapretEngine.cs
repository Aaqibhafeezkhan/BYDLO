using System.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Installation;

namespace Bydlo.Engines.Zapret;

/// <summary>
/// Zapret DPI bypass using Flowseal/zapret-discord-youtube on Windows: runs <c>bin/winws.exe</c> (bol-van zapret + WinDivert)
/// with the arguments of the selected upstream strategy. Linux / macOS native zapret (nfqws / tpws) is planned.
/// </summary>
public sealed class ZapretEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges)
    : ProcessEngineBase(runner, redactor)
{
    /// <summary>Other DPI tools that conflict with winws (same list as Flowseal's diagnostics).</summary>
    public static readonly string[] ConflictingProcesses = ["winws", "goodbyedpi"];

    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.Zapret,
        DisplayName = "Zapret (Flowseal)",
        Upstream = "https://github.com/Flowseal/zapret-discord-youtube",
        License = "MIT",
        IsTool = true,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Planned,
            [OsFamily.MacOS] = SupportLevel.Planned,
            [OsFamily.Android] = SupportLevel.Unsupported,
        },
    };

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => false;

    /// <summary>Resolves the installation: explicit setting, else the BYDLO-managed pinned release.</summary>
    public ZapretInstallation? ResolveInstallation(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Zapret.InstallPath))
        {
            return new ZapretInstallation(settings.Zapret.InstallPath);
        }

        var managed = locator.Installer.InstalledExecutable("zapret", locator.Platform);
        return managed is null ? null : new ZapretInstallation(Path.GetDirectoryName(Path.GetDirectoryName(managed)!)!);
    }

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        var os = locator.Platform.Os;
        if (Info.SupportOn(os) == SupportLevel.Unsupported)
        {
            return Task.FromResult(EngineAvailability.Unsupported("Zapret needs packet interception, which is not available on this platform."));
        }

        if (Info.SupportOn(os) == SupportLevel.Planned)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("Native zapret (nfqws / tpws) for this platform is planned."));
        }

        var install = ResolveInstallation(settings);
        if (install is null || !install.IsValid)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "Zapret is not installed. Download it in Tools → Zapret or choose an extracted Flowseal release folder."));
        }

        if (string.IsNullOrWhiteSpace(settings.Zapret.Strategy) || !File.Exists(Path.Combine(install.Root, settings.Zapret.Strategy)))
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "Choose a Zapret strategy first.", install.WinwsPath));
        }

        if (!privileges.IsElevated)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NeedsElevation, "Zapret (WinDivert) needs administrator rights.", install.WinwsPath));
        }

        return Task.FromResult(EngineAvailability.Ready(install.WinwsPath));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var install = ResolveInstallation(request.Settings)!;
        var running = ConflictingProcesses.SelectMany(n => SafeGetProcesses(n)).ToList();
        if (running.Count > 0)
        {
            throw new EngineException($"Another DPI bypass is already running ({string.Join(", ", running.Distinct())}). Stop it first (e.g. Flowseal service.bat → Remove Services).");
        }

        install.EnsureUserLists();
        var text = await File.ReadAllTextAsync(Path.Combine(install.Root, request.Settings.Zapret.Strategy!), ct).ConfigureAwait(false);
        var parsed = ZapretStrategyParser.Parse(text, install.Root, GameFilterValues.From(request.Settings.Zapret.GameFilter));
        foreach (var w in parsed.Warnings)
        {
            EmitLog("warning: " + w);
        }

        return new ProcessSpec
        {
            FileName = install.WinwsPath,
            Arguments = parsed.Arguments,
            WorkingDirectory = install.BinDirectory,
            DisplayName = "winws",
        };
    }

    protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(5);

    protected override bool IsReadyLine(string line) => line.Contains("capture is started", StringComparison.OrdinalIgnoreCase);

    protected override string? TryParseFatal(string line) =>
        line.Contains("windivert", StringComparison.OrdinalIgnoreCase) && line.Contains("error", StringComparison.OrdinalIgnoreCase) ? line
        : line.StartsWith("could not", StringComparison.OrdinalIgnoreCase) ? line
        : null;

    private static IEnumerable<string> SafeGetProcesses(string name)
    {
        Process[] found;
        try
        {
            found = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        foreach (var p in found)
        {
            p.Dispose();
            yield return name;
        }
    }
}

/// <summary>Downloads the pinned Flowseal release and keeps the user's lists across updates.</summary>
public sealed class ZapretUpdater(EngineInstaller installer, PlatformInfo platform)
{
    public async Task<ZapretInstallation> InstallOrUpdateAsync(ZapretInstallation? previous, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var exe = await installer.InstallAsync("zapret", platform, progress, ct).ConfigureAwait(false);
        var install = new ZapretInstallation(Path.GetDirectoryName(Path.GetDirectoryName(exe)!)!);
        if (previous is not null && previous.IsValid && !string.Equals(previous.Root, install.Root, StringComparison.OrdinalIgnoreCase))
        {
            install.ImportUserListsFrom(previous);
        }

        install.EnsureUserLists();
        return install;
    }
}
