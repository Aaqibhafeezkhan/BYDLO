using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines;
using Bydlo.Engines.Installation;
using Bydlo.Platform;

namespace Bydlo.Helper;

internal static class HelperServer
{
    private static readonly HelperLeasePool Leases = new();
    private static readonly string[] SystemToolDirectories = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    public static async Task RunAsync(CancellationToken ct)
    {
        NetworkSafetyGuard.EnsureAllowed("serve privileged requests");
        if (!OperatingSystem.IsWindows()) { UnixHelperSecurity.EnsureProtectedParents(HelperPaths.InstallDirectory); }
        var owner = File.ReadAllText(Path.Combine(HelperPaths.InstallDirectory, "owner.sid")).Trim();
        using var http = new HttpClient(); // No downloads are exposed by this host.
        var paths = new RootedAppPaths(Path.Combine(HelperPaths.InstallDirectory, "data"));
        Directory.CreateDirectory(paths.DataDirectory);
        using var ownership = new FileStream(Path.Combine(paths.DataDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var installer = new EngineInstaller(http, EngineLock.LoadEmbedded(), paths.EnginesDirectory);
        var journal = new ProcessJournal(paths.JournalFile);
        var runner = new VerifiedHelperRunner(new SystemProcessRunner(journal));
        var locator = new BundleOnlyLocator(installer);
        var privileges = new PrivilegeService();
        var firewall = new ZapretFirewall(runner, installer.UserDataDirectory);
        var network = AmneziaNetworkFactory.Create(runner, paths);
        var registry = EngineRegistryFactory.Create(runner, LogRedactor.Shared, locator, privileges,
            zapretFirewall: firewall,
            amneziaNetwork: network,
            openVpnDriver: new OpenVpnDriver(runner, privileges, () => locator.BundledExecutable("openvpn") is { } exe ? Path.GetDirectoryName(exe) : null));
        // Fail before accepting clients if an installed bundle was changed or is incomplete.
        VerifyBundle();
        journal.KillOrphans();
        if (journal.FindOrphans().Count != 0) { throw new IOException("Previous helper engines could not be stopped."); }
        if (network.HasPendingRecovery) { await network.RemoveAsync(ct).ConfigureAwait(false); }
        if (firewall.HasPendingRecovery) { await firewall.RemoveAsync().ConfigureAwait(false); }
        var sessions = new List<Task>();
        try
        {
            await foreach (var stream in HelperListener.AcceptAsync(owner, ct).ConfigureAwait(false))
            {
                foreach (var finished in sessions.Where(t => t.IsCompleted)) { await finished.ConfigureAwait(false); }
                sessions.RemoveAll(t => t.IsCompleted);
                if (sessions.Count >= 4) { await stream.DisposeAsync().ConfigureAwait(false); continue; }
                sessions.Add(ServeAsync(stream, registry, paths.SessionsDirectory, ct));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { await Task.WhenAll(sessions).ConfigureAwait(false); }
    }

    private static async Task ServeAsync(Stream pipe, EngineRegistry registry, string root, CancellationToken ct)
    {
        await using var stream = pipe;
        await using var session = new HelperSession(kind => registry.Create(kind, new AppSettings()), root, Leases);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(90)); // Abandoned leases never run indefinitely.
                var request = await HelperProtocol.ReadAsync<HelperRequest>(pipe, deadline.Token).ConfigureAwait(false);
                try
                {
                    var reply = await session.DispatchAsync(request, deadline.Token).ConfigureAwait(false);
                    await HelperProtocol.WriteAsync(pipe, reply, deadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never serialize exceptions: they may contain credentials, configs or privileged paths.
                    await HelperProtocol.WriteAsync(pipe, new HelperReply { State = EngineState.Failed, Error = "helper.error" }, deadline.Token).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { }
    }

    public static void VerifyBundle()
    {
        var root = HelperPaths.InstallDirectory;
        var hashes = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "bundle.sha256.json")))
            ?? throw new InvalidDataException("Missing bundle hashes.");
        HelperBundleIntegrity.Verify(root, hashes);
    }

    private sealed class BundleOnlyLocator(EngineInstaller installer) : EngineLocator(installer, PlatformInfo.Current, HelperPaths.InstallDirectory)
    {
        public override string? Locate(EngineKind kind, AppSettings settings) =>
            LockId(kind) is { } id ? BundledExecutable(id) : null;
    }

    private sealed class VerifiedHelperRunner(IProcessRunner inner) : IProcessRunner
    {
        public IManagedProcess Start(ProcessSpec spec) { Verify(spec); return inner.Start(spec); }
        public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default)
        {
            // These scripts are generated exclusively by AmneziaNetwork after typed IP/GUID validation.
            // No RPC operation accepts a ProcessSpec or command text.
            if (OperatingSystem.IsWindows() && spec.FileName == "powershell.exe" && spec.DisplayName == "amnezia-network")
            {
                spec = spec with { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") };
                NetworkSafetyGuard.EnsureAllowed("configure the BYDLO tunnel");
            }
            else if (!OperatingSystem.IsWindows() && spec.DisplayName is "amnezia-network" or "Zapret firewall")
            {
                var name = Path.GetFileName(spec.FileName);
                if (name is not ("ip" or "resolvectl" or "ifconfig" or "route" or "scutil" or "networksetup" or "pfctl" or "nft"))
                { throw new UnauthorizedAccessException("Unapproved system tool."); }
                var absolute = SystemToolDirectories.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists)
                    ?? throw new FileNotFoundException("Required system tool is missing.");
                spec = spec with { FileName = absolute };
                NetworkSafetyGuard.EnsureAllowed("configure BYDLO network resources");
            }
            else { Verify(spec); }
            return inner.RunToCompletionAsync(spec, timeout, ct);
        }
        private static void Verify(ProcessSpec spec)
        {
            NetworkSafetyGuard.EnsureAllowed("launch a bundled helper engine");
            VerifyBundle();
            var relative = Path.GetRelativePath(Path.Combine(HelperPaths.InstallDirectory, "engines"), spec.FileName);
            if (!Path.IsPathFullyQualified(spec.FileName) || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathFullyQualified(relative))
            { throw new UnauthorizedAccessException("Helper can only launch bundled engines."); }
        }
    }
}
