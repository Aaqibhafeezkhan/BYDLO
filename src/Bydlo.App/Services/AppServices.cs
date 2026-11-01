using Avalonia.Controls;
using Bydlo.Core.Connection;
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
public sealed class AppServices
{
    private AppServices(string[] args)
    {
        Args = args;
        ForcedMock = args.Contains("--mock") || Environment.GetEnvironmentVariable("BYDLO_MOCK") == "1";
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
    public AppSettings EffectiveSettings
    {
        get
        {
            Settings.MockMode = IsMock;
            return Settings;
        }
    }

    public static async Task<AppServices> CreateAsync(string[] args)
    {
        var s = new AppServices(args);
        s.Settings = await s.SettingsStore.LoadAsync().ConfigureAwait(false);
        await s.Profiles.LoadAsync().ConfigureAwait(false);
        if (!s.IsMock && !NetworkSafetyGuard.IsLocked && s.Privileges.IsElevated && s.ZapretFirewall.HasPendingRecovery)
        {
            try { await s.ZapretFirewall.RemoveAsync().ConfigureAwait(false); }
            catch (Exception ex) { s.Log.Error("zapret", "Firewall recovery failed: " + ex.Message); }
        }

        s.Orphans = s.Journal.FindOrphans();
        s.Log.Info("app", $"BYDLO started. Data: {s.Paths.DataDirectory}. Mock mode: {s.IsMock}. Safety lock: {NetworkSafetyGuard.IsLocked}.");
        return s;
    }

    public async Task SaveSettingsAsync()
    {
        // Never persist a command-line forced mock mode as the user's choice.
        var persisted = Settings.MockMode;
        if (ForcedMock)
        {
            Settings.MockMode = false;
        }

        await SettingsStore.SaveAsync(Settings).ConfigureAwait(false);
        Settings.MockMode = persisted;
    }

    public int CleanupOrphans()
    {
        var n = Journal.KillOrphans();
        Orphans = [];
        Log.Warn("app", $"Stopped {n} engine process(es) left by a previous session.");
        return n;
    }

    public async Task ShutdownAsync()
    {
        await Connections.StopAllAsync().ConfigureAwait(false);
        Http.Dispose();
    }
}
