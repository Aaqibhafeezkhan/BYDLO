using Bydlo.Platform.Processes;
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
    private static readonly HelperBuild Build = HelperManagement.GetBuild(EngineLock.BundleVersion);
    private static readonly string[] SystemToolDirectories = ["/usr/sbin", "/sbin", "/usr/bin", "/bin"];
    private static readonly List<HelperSession> PendingCleanups = [];
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
                foreach (var finished in sessions.Where(t => t.IsCompleted))
                {
                    try { await finished.ConfigureAwait(false); }
                    catch (Exception) { Console.Error.WriteLine("A helper session failed to finish cleanup."); }
                }
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
        await RetryPendingCleanupsAsync().ConfigureAwait(false);
        await using var stream = pipe;
        var session = new HelperSession(kind => registry.Create(kind, new AppSettings()), root, Leases);
        try
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(90)); // Abandoned leases never run indefinitely.
                    var request = await HelperProtocol.ReadAsync<HelperRequest>(pipe, deadline.Token).ConfigureAwait(false);
                    if (request.Version != HelperProtocol.Version ||
                        (request.Operation is HelperOperation.Check or HelperOperation.Start or HelperOperation.Reconnect && request.ExpectedBuild != Build))
                    {
                        await HelperProtocol.WriteAsync(pipe, new HelperReply
                        {
                            Build = Build, State = EngineState.Failed, Error = HelperProtocol.Outdated,
                        }, deadline.Token).ConfigureAwait(false);
                        continue;
                    }
                    try
                    {
                        var reply = await session.DispatchAsync(request, deadline.Token).ConfigureAwait(false);
                        await HelperProtocol.WriteAsync(pipe, reply with { Build = Build }, deadline.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Never serialize exceptions: they may contain credentials, configs or privileged paths.
                        await HelperProtocol.WriteAsync(pipe, new HelperReply { Build = Build, State = EngineState.Failed, Error = "helper.error" }, deadline.Token).ConfigureAwait(false);
                        // Keep the authenticated lease available for cleanup retries.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException) { }
        }
        finally
        {
            // A failed cleanup stays retryable: keep the session so the next
            // connection can retry Dispose instead of leaking the lease.
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                Console.Error.WriteLine("A helper session failed to finish cleanup.");
                lock (PendingCleanups) { PendingCleanups.Add(session); }
            }
        }
    }

    private static async Task RetryPendingCleanupsAsync()
    {
        List<HelperSession> pending;
        lock (PendingCleanups)
        {
            if (PendingCleanups.Count == 0) { return; }
            pending = [.. PendingCleanups];
            PendingCleanups.Clear();
        }
        foreach (var session in pending)
        {
            try { await session.DisposeAsync().ConfigureAwait(false); }
            catch (Exception)
            {
                Console.Error.WriteLine("A helper session failed to finish cleanup.");
                lock (PendingCleanups) { PendingCleanups.Add(session); }
            }
        }
    }

    public static void VerifyBundle()
    {
        var root = HelperPaths.InstallDirectory;
        var hashes = System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "bundle.sha256.json")), HelperJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidDataException("Missing bundle hashes.");
        HelperBundleIntegrity.Verify(root, hashes);
    }

    private sealed class BundleOnlyLocator(EngineInstaller installer) : EngineLocator(installer, PlatformInfo.Current, HelperPaths.InstallDirectory)
    {
        public override string? Locate(EngineKind kind, AppSettings settings) =>
            LockId(kind) is { } id ? BundledExecutable(id) : null;
    }

    // Pure resolution policy shared with isolated tests. Execution stays guarded in VerifiedHelperRunner.
    internal static ProcessSpec? ResolveUnixSystemTool(ProcessSpec spec, Func<string, bool> fileExists)
    {
        if (spec.DisplayName is not ("amnezia-network" or "Zapret firewall")) { return null; }
        var name = Path.GetFileName(spec.FileName);
        if (name is not ("ip" or "resolvectl" or "ifconfig" or "route" or "scutil" or "networksetup" or "pfctl" or "nft"))
        { throw new UnauthorizedAccessException("Unapproved system tool."); }
        var absolute = SystemToolDirectories.Select(dir => Path.Combine(dir, name)).FirstOrDefault(fileExists)
            ?? throw new FileNotFoundException("Required system tool is missing.");
        return spec with { FileName = absolute };
    }

    internal sealed class VerifiedHelperRunner(IProcessRunner inner) : IProcessRunner
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
            else if (!OperatingSystem.IsWindows() && ResolveUnixSystemTool(spec, File.Exists) is { } systemTool)
            {
                spec = systemTool;
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
