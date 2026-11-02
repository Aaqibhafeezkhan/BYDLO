using Avalonia.Controls;
using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Import;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;
using Bydlo.Engines;
using Bydlo.Engines.Installation;
using Bydlo.Engines.TgWsProxy;
using Bydlo.Engines.Zapret;
using Bydlo.Platform;

namespace Bydlo.App.Services;

/// <summary>Composition root: creates every service once. No DI container on purpose — the graph is small.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "ShutdownAsync owns the asynchronous application lifetime and disposes its resources.")]
public sealed class AppServices
{
    private AppServices(string[] args)
    {
        Args = args;
        ForcedMock = AppSettings.IsMockForced(args, Environment.GetEnvironmentVariable("BYDLO_MOCK"));
        Paths = AppPathsFactory.Create();
        Log = new LogHub(LogRedactor.Shared, Paths.LogsDirectory);
        Secrets = SecretStoreFactory.Create(Paths);
        SettingsStore = new SettingsStore(Paths.SettingsFile);
        Profiles = new ProfileStore(Paths.ProfilesFile, Secrets);
        Privileges = new PrivilegeService();
        Journal = new ProcessJournal(Paths.JournalFile);
        Runner = new SystemProcessRunner(Journal);
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BYDLO/0.1");
        Lock = EngineLock.LoadEmbedded();
        Installer = new EngineInstaller(Http, Lock, Paths.EnginesDirectory);
        Locator = new EngineLocator(Installer, PlatformInfo.Current);
        ZapretFirewall = new ZapretFirewall(Runner, Installer.UserDataDirectory);
        TgRuntime = new TgWsProxyRuntime(Paths.EnginesDirectory, Lock, Runner);
        TgSecrets = new TgWsProxySecrets(Secrets);
        Registry = EngineRegistryFactory.Create(Runner, LogRedactor.Shared, Locator, Privileges, TgRuntime, () => new MockEngine(), ZapretFirewall);
        SystemProxy = SystemProxyFactory.Create();
        Connections = new ConnectionManager(Registry, Secrets, Paths, new ConflictDetector(new TcpPortChecker()), Log, SystemProxy);
        ConnectionCheck = new ConnectionCheckService();
        Importer = new ProfileImporter();
        Subscriptions = new SubscriptionClient(Http, Importer);
        Zapret = new ZapretEngine(Runner, LogRedactor.Shared, Locator, Privileges, ZapretFirewall);
        ZapretUpdater = new ZapretUpdater(Installer, PlatformInfo.Current);
    }

    public string[] Args { get; }

    /// <summary>Mock Mode forced for this session by <c>--mock</c> / <c>BYDLO_MOCK=1</c>.</summary>
    public bool ForcedMock { get; }

    public IAppPaths Paths { get; }

    public LogHub Log { get; }

    public ISecretStore Secrets { get; }

    public SettingsStore SettingsStore { get; }

    public AppSettings Settings { get; private set; } = new();

    public ProfileStore Profiles { get; }

    public IPrivilegeService Privileges { get; }

    public ProcessJournal Journal { get; }

    public IProcessRunner Runner { get; }

    public HttpClient Http { get; }

    public EngineLock Lock { get; }

    public EngineInstaller Installer { get; }

    public EngineLocator Locator { get; }

    public ZapretFirewall ZapretFirewall { get; }

    public TgWsProxyRuntime TgRuntime { get; }

    public TgWsProxySecrets TgSecrets { get; }

    public EngineRegistry Registry { get; }

    public ISystemProxyService SystemProxy { get; }

    public ConnectionManager Connections { get; }

    /// <summary>Explicit external-IP check used by Home. Never runs automatically.</summary>
    public ConnectionCheckService ConnectionCheck { get; }

    public ProfileImporter Importer { get; }

    public SubscriptionClient Subscriptions { get; }

    /// <summary>Used for Zapret strategy / install queries (not for running).</summary>
    public ZapretEngine Zapret { get; }

    public ZapretUpdater ZapretUpdater { get; }

    public ThemeService Theme { get; } = new();

    /// <summary>Engines left running by a previous crashed session.</summary>
    public IReadOnlyList<JournalEntry> Orphans { get; private set; } = [];

    /// <summary>Set by the root view so services can reach clipboard / storage / launcher.</summary>
    public Func<TopLevel?> TopLevel { get; set; } = () => null;

    public bool IsMock => ForcedMock || Settings.MockMode;

    /// <summary>Settings passed to engines (Mock Mode forced by the command line wins).</summary>
    public AppSettings EffectiveSettings => Settings.WithMockMode(IsMock);

    public static async Task<AppServices> CreateAsync(string[] args)
    {
        var s = new AppServices(args);
        var firstRun = !File.Exists(s.Paths.SettingsFile);
        s.Settings = await s.SettingsStore.LoadAsync().ConfigureAwait(false);
        if (firstRun)
        {
            s.Settings.OnboardingCompleted = false;
            s.Settings.Language = "auto";
            s.Settings.Theme = ThemePreference.System;
        }

        await s.Profiles.LoadAsync().ConfigureAwait(false);
        if (!s.IsMock && !NetworkSafetyGuard.IsLocked && s.Privileges.IsElevated && s.ZapretFirewall.HasPendingRecovery)
        {
            try { await s.ZapretFirewall.RemoveAsync().ConfigureAwait(false); }
            catch (Exception ex) { s.Log.Error("zapret", "Firewall recovery failed: " + ex.Message); }
        }

        if (!s.IsMock && !NetworkSafetyGuard.IsLocked)
        {
            // A retained TUN may outlive a crashed UI. Release only BYDLO's journaled guard.
            try { s.Journal.KillOrphans(RetainedTunEngine.GuardProcessName); }
            catch (Exception ex) { s.Log.Error("kill-switch", "Crash recovery failed: " + ex.Message); }
        }
        s.Orphans = s.Journal.FindOrphans();
        s.Log.Info("app", $"BYDLO started. Data: {s.Paths.DataDirectory}. Mock mode: {s.IsMock}. Safety lock: {NetworkSafetyGuard.IsLocked}.");
        return s;
    }

    public Task SaveSettingsAsync() => SettingsStore.SaveAsync(Settings);

    private readonly CancellationTokenSource _shutdownCancellation = new();

    public CancellationToken ShutdownToken => _shutdownCancellation.Token;

    public void CleanupKillSwitch()
    {
        if (IsMock || NetworkSafetyGuard.IsLocked) { return; }
        Journal.KillOrphans(RetainedTunEngine.GuardProcessName);
        if (Journal.FindOrphans().Any(e => e.DisplayName == RetainedTunEngine.GuardProcessName))
        {
            throw new InvalidOperationException(Loc.Instance["settings.killswitch.recovery"]);
        }
    }

    public int CleanupOrphans()
    {
        var n = Journal.KillOrphans();
        Orphans = [];
        Log.Warn("app", $"Stopped {n} engine process(es) left by a previous session.");
        return n;
    }

    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;

    public Task ShutdownAsync()
    {
        lock (_shutdownGate) { return _shutdownTask ??= ShutdownCoreAsync(); }
    }

    private async Task ShutdownCoreAsync()
    {
        _shutdownCancellation.Cancel();
        try { await Connections.StopAllAsync().ConfigureAwait(false); }
        finally { Http.Dispose(); _shutdownCancellation.Dispose(); }
    }
}
