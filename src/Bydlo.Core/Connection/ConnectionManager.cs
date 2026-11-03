using System.Text.Json;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Core.Connection;

public enum ConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Reconnecting,
    Error,
}

/// <summary>Snapshot of one session (primary tunnel or a tool).</summary>
public sealed record SessionSnapshot(
    string Key,
    ConnectionStatus Status,
    Profile? Profile,
    EngineKind? Engine,
    bool IsMock,
    DateTimeOffset? ConnectedSince,
    string? Error,
    TrafficSample? LastTraffic,
    ConnectionMode Mode = ConnectionMode.LocalProxy,
    int LocalProxyPort = 0)
{
    public string? ErrorLocalizationKey { get; init; }
    public bool CleanupPending { get; init; }

    /// <summary>Active relay chain, if the session carries chained traffic (issue #75).</summary>
    public ProxyChain? Chain { get; init; }
    public ServerGroup? Group { get; init; }
    public Profile? SelectedGroupProfile { get; init; }
}

/// <summary>
/// The single entry point for starting and stopping anything that carries traffic.
/// Owns one primary tunnel plus independent tool sessions (Zapret, TgWsProxy), checks conflicts,
/// supervises engines and reconnects after crashes.
/// </summary>
public sealed partial class ConnectionManager : IAsyncDisposable
{
    public const string PrimaryKey = "primary";
    public const string ZapretKey = "zapret";
    public const string TgWsProxyKey = "tgwsproxy";
    public const string TgWsProxySecretId = "tool:tgwsproxy";
    public const string LocalProxySecretId = "tool:localproxy";

    private const int MaxReconnectAttempts = 3;

    private readonly EngineRegistry _registry;
    private readonly ISecretStore _secrets;
    private readonly IAppPaths _paths;
    private readonly ConflictDetector _conflicts;
    private readonly LogHub _log;
    private readonly ISystemProxyService _systemProxy;
    private readonly OsFamily _os;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public ConnectionManager(
        EngineRegistry registry,
        ISecretStore secrets,
        IAppPaths paths,
        ConflictDetector conflicts,
        LogHub log,
        ISystemProxyService? systemProxy = null,
        OsFamily? os = null)
    {
        _registry = registry;
        _secrets = secrets;
        _paths = paths;
        _conflicts = conflicts;
        _log = log;
        _systemProxy = systemProxy ?? new UnsupportedSystemProxyService();
        _os = os ?? PlatformInfo.Current.Os;
    }

    /// <summary>Raised (from any thread) whenever a session changes.</summary>
    public event EventHandler<SessionSnapshot>? SessionChanged;

    /// <summary>Delay before reconnect attempt N (1-based). Tests shorten it.</summary>
    public Func<int, TimeSpan> ReconnectDelay { get; set; } = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    // Tests replace only this check and use a recording proxy service; the global lock stays on.
    internal Action EnsureSystemProxyAllowed { get; init; } =
        () => NetworkSafetyGuard.EnsureAllowed("set the system proxy");

    public SessionSnapshot Primary => Snapshot(PrimaryKey);

    /// <summary>Publishes a core-confirmed member; no probes or network operations occur here.</summary>
    public void RefreshGroupSelection()
    {
        Session? session;
        lock (_sessions) { _sessions.TryGetValue(PrimaryKey, out session); }
        if (session?.Group is not null && session.Status == ConnectionStatus.Connected) { Publish(session); }
    }

    public async Task ConnectGroupAsync(ServerGroup group, IReadOnlyList<ChainHop> members, AppSettings settings, CancellationToken ct = default)
    {
        group = JsonSerializer.Deserialize<ServerGroup>(JsonSerializer.Serialize(group))!;
        members = JsonSerializer.Deserialize<List<ChainHop>>(JsonSerializer.Serialize(members))!;
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Config.ServerGroupSupport.Validate(group, members, group.Engine);
        var profiles = members.Select(m => m.Profile).ToList();
        if (group.SubscriptionId is { } subscription && profiles.Any(p => p.SubscriptionId != subscription)
            || group.SubscriptionId is null && !group.ProfileIds.SequenceEqual(profiles.Select(p => p.Id)))
        { throw ServerGroup.Invalid(); }
        var blocking = PreviewChainConflicts(profiles, settings).FirstOrDefault(i => i.Severity == ConflictSeverity.Blocking);
        if (blocking is not null) { throw new EngineException(blocking.Message); }
        var auth = await LoadLocalProxyAuthAsync(settings, profiles[0], ct).ConfigureAwait(false);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Validate engine selection before replacing a working connection.
            var engine = _registry.CreateForChain(profiles, group.Engine, settings, _os);
            try { await StopSessionUnsafeAsync(PrimaryKey).ConfigureAwait(false); }
            catch { await engine.DisposeAsync().ConfigureAwait(false); throw; }
            var session = new Session(PrimaryKey, engine, profiles[0], members[0].Secrets, settings, auth,
                group: group, groupMembers: members);
            await StartSessionUnsafeAsync(session, ct).ConfigureAwait(false);
            await SetSystemProxyUnsafeAsync(session, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ActiveConnection>?> ListActiveConnectionsAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return ActiveConnectionSource() is { } source ? await source.ListConnectionsAsync(ct).ConfigureAwait(false) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseActiveConnectionAsync(string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ActiveConnectionSource() is { } source)
            {
                await source.CloseConnectionAsync(id, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private IActiveConnectionSource? ActiveConnectionSource()
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(PrimaryKey, out var session) && session.Status == ConnectionStatus.Connected
                ? session.Engine as IActiveConnectionSource : null;
        }
    }

    public SessionSnapshot Zapret => Snapshot(ZapretKey);

    public SessionSnapshot TgWsProxy => Snapshot(TgWsProxyKey);

    public SessionSnapshot Snapshot(string key)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(key, out var s) ? s.ToSnapshot() : new SessionSnapshot(key, ConnectionStatus.Disconnected, null, null, false, null, null, null);
        }
    }

    public IReadOnlyList<ConflictIssue> PreviewConflicts(Profile profile, AppSettings settings)
    {
        var issues = _conflicts.CheckPrimary(profile, settings, IsActive(ZapretKey), IsActive(TgWsProxyKey));
        return FilterOwnPrimaryPort(issues, settings);
    }

    /// <summary>Aggregates per-hop checks for a relay chain (deduped by issue code).</summary>
    public IReadOnlyList<ConflictIssue> PreviewChainConflicts(IReadOnlyList<Profile> hops, AppSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var issues = new List<ConflictIssue>();
        foreach (var hop in hops)
        {
            foreach (var issue in _conflicts.CheckPrimary(hop, settings, IsActive(ZapretKey), IsActive(TgWsProxyKey)))
            {
                if (seen.Add(issue.Code))
                {
                    issues.Add(issue);
                }
            }
        }

        return FilterOwnPrimaryPort(issues, settings);
    }

    /// <summary>An active primary owns its local proxy port; reconnecting on the same port is not a conflict (issue #202).</summary>
    private List<ConflictIssue> FilterOwnPrimaryPort(IReadOnlyList<ConflictIssue> issues, AppSettings settings)
    {
        if (issues.Count == 0 || !IsActive(PrimaryKey))
        {
            return [.. issues];
        }

        var ownPort = Snapshot(PrimaryKey).LocalProxyPort;
        if (ownPort == 0 || ownPort != settings.LocalProxyPort)
        {
            return [.. issues];
        }

        return issues.Where(i => !string.Equals(i.Code, "port.local-proxy", StringComparison.Ordinal)).ToList();
    }

    /// <summary>Connects the primary tunnel. Disconnects a previous primary first.</summary>
    public async Task ConnectAsync(Profile profile, AppSettings settings, CancellationToken ct = default)
    {
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        var blocking = PreviewConflicts(profile, settings).Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        if (blocking.Count > 0)
        {
            Fail(PrimaryKey, profile, settings, string.Join(" ", blocking.Select(b => b.Message)));
            throw new EngineException(blocking[0].Message);
        }

        var lanAuth = await LoadLocalProxyAuthAsync(settings, profile, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var secrets = await _secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false);
            var engine = _registry.CreateFor(profile, settings, _os);
            try { await StopSessionUnsafeAsync(PrimaryKey).ConfigureAwait(false); }
            catch { await engine.DisposeAsync().ConfigureAwait(false); throw; }
            var session = new Session(PrimaryKey, engine, profile, secrets, settings, lanAuth);
            await StartSessionUnsafeAsync(session, ct).ConfigureAwait(false);

            await SetSystemProxyUnsafeAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Connects a relay chain (entry first, exit last). Disconnects a previous primary first.</summary>
    public async Task ConnectChainAsync(ProxyChain chain, IReadOnlyList<ChainHop> hops, AppSettings settings, CancellationToken ct = default)
    {
        settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        if (hops.Count != chain.ProfileIds.Count)
        {
            throw new EngineException("Chain hops do not match the saved chain.", localizationKey: "error.chain.unknown");
        }

        for (var i = 0; i < hops.Count; i++)
        {
            if (hops[i].Profile.Id != chain.ProfileIds[i])
            {
                throw new EngineException("Chain hops do not match the saved chain.", localizationKey: "error.chain.unknown");
            }
        }

        ChainValidator.ThrowIfInvalid(chain, id => hops.FirstOrDefault(h => h.Profile.Id == id)?.Profile);
        var hopProfiles = hops.Select(h => h.Profile).ToList();
        var blocking = PreviewChainConflicts(hopProfiles, settings).Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        if (blocking.Count > 0)
        {
            Fail(PrimaryKey, hopProfiles[^1], settings, string.Join(" ", blocking.Select(b => b.Message)));
            throw new EngineException(blocking[0].Message);
        }

        var exit = hopProfiles[^1];
        var lanAuth = await LoadLocalProxyAuthAsync(settings, exit, ct).ConfigureAwait(false);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var engine = _registry.CreateForChain(hopProfiles, chain.PreferredEngine, settings, _os);
            try { await StopSessionUnsafeAsync(PrimaryKey).ConfigureAwait(false); }
            catch { await engine.DisposeAsync().ConfigureAwait(false); throw; }
            var session = new Session(PrimaryKey, engine, exit, hops[^1].Secrets, settings, lanAuth, chain, hops);
            await StartSessionUnsafeAsync(session, ct).ConfigureAwait(false);

            await SetSystemProxyUnsafeAsync(session, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SetSystemProxyUnsafeAsync(Session session, CancellationToken ct)
    {
        var settings = session.Settings;
        if (!settings.SetSystemProxy || settings.Mode != ConnectionMode.LocalProxy || session.IsMock || !_systemProxy.IsSupported)
        {
            return;
        }

        try
        {
            EnsureSystemProxyAllowed();
            // SetAsync can partially apply the proxy before throwing; retain ownership for cleanup/retry.
            session.SystemProxySet = true;
            await _systemProxy.SetAsync("127.0.0.1",
                ProxyPorts.HttpPort(settings, session.Engine.Descriptor.Kind), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var message = _log.Redactor.Redact(ex.Message);
            _log.Error(session.Key, message);
            try { await StopSessionUnsafeAsync(session.Key).ConfigureAwait(false); }
            catch (Exception stopError)
            {
                throw new AggregateException(message, stopError);
            }
            Fail(session.Key, session.Profile, settings, message, (ex as EngineException)?.LocalizationKey ?? "error.systemproxy.failed");
            throw ex as EngineException ?? new EngineException(message, ex, "error.systemproxy.failed");
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopSessionUnsafeAsync(PrimaryKey).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocalProxyAuth?> LoadLocalProxyAuthAsync(AppSettings settings, Profile profile, CancellationToken ct)
    {
        if (!settings.AllowLan || settings.MockMode)
        {
            return null;
        }

        var stored = await _secrets.LoadAsync(LocalProxySecretId, ct).ConfigureAwait(false);
        var key = LocalProxyAuthValidator.Validate(settings, stored["password"]);
        if (key is not null)
        {
            var message = key == LocalProxyAuthValidator.LoginRequiredKey
                ? "LAN access is on but the proxy login is empty. Set it in Settings → Advanced."
                : "LAN access is on but the proxy password is missing. Set it in Settings → Advanced.";
            Fail(PrimaryKey, profile, settings, message, key);
            throw new EngineException(message, localizationKey: key);
        }

        return new LocalProxyAuth(settings.LocalProxyUsername.Trim(), stored["password"]!);
    }

    public Task StartZapretAsync(AppSettings settings, CancellationToken ct = default) =>
        StartToolAsync(ZapretKey, EngineKind.Zapret, settings, _conflicts.CheckZapret(settings, IsActive(PrimaryKey)), ct);

    public Task StartTgWsProxyAsync(AppSettings settings, CancellationToken ct = default) =>
        StartToolAsync(TgWsProxyKey, EngineKind.TgWsProxy, settings, _conflicts.CheckTgWsProxy(settings, IsActive(PrimaryKey)), ct);

    /// <summary>Start once and return the credentials of the actual running session.</summary>
    public async Task<(TgWsProxySettings Settings, string Secret)> EnsureTgWsProxyAsync(AppSettings settings, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TgWsProxy.Status != ConnectionStatus.Connected || TgWsProxy.CleanupPending)
            {
                await StartToolUnsafeAsync(TgWsProxyKey, EngineKind.TgWsProxy, CloneSettings(settings),
                    _conflicts.CheckTgWsProxy(settings, IsActive(PrimaryKey)), ct).ConfigureAwait(false);
            }

            var session = _sessions[TgWsProxyKey];
            return (CloneSettings(session.Settings).TgWsProxy, session.Secrets["secret"]!);
        }
        finally { _gate.Release(); }
    }

    public async Task<(TgWsProxySettings Settings, string Secret)?> GetRunningTgWsProxyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (TgWsProxy.Status != ConnectionStatus.Connected || TgWsProxy.CleanupPending ||
                !_sessions.TryGetValue(TgWsProxyKey, out var session)) { return null; }

            return (CloneSettings(session.Settings).TgWsProxy, session.Secrets["secret"]!);
        }
        finally { _gate.Release(); }
    }

    public async Task StopToolAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopSessionUnsafeAsync(key).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var errors = new List<Exception>();
            foreach (var key in new[] { PrimaryKey, ZapretKey, TgWsProxyKey })
            {
                try { await StopSessionUnsafeAsync(key).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add(ex); }
            }
            await RetrySpeedProbeCleanupAsync(errors).ConfigureAwait(false);
            if (errors.Count > 0) { throw new AggregateException("Session cleanup failed.", errors); }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StartToolAsync(string key, EngineKind kind, AppSettings settings, IReadOnlyList<ConflictIssue> issues, CancellationToken ct)
    {
        settings = CloneSettings(settings);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await StartToolUnsafeAsync(key, kind, settings, issues, ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StartToolUnsafeAsync(string key, EngineKind kind, AppSettings settings, IReadOnlyList<ConflictIssue> issues, CancellationToken ct)
    {
        foreach (var w in issues.Where(i => i.Severity == ConflictSeverity.Warning))
        {
            _log.Warn(key, w.Message);
        }

        var blocking = issues.FirstOrDefault(i => i.Severity == ConflictSeverity.Blocking);
        if (blocking is not null)
        {
            Fail(key, null, settings, blocking.Message);
            throw new EngineException(blocking.Message);
        }

        await StopSessionUnsafeAsync(key).ConfigureAwait(false);
        var engine = _registry.Create(kind, settings);
        var secrets = await _secrets.LoadAsync(key == TgWsProxyKey ? TgWsProxySecretId : key, ct).ConfigureAwait(false);
        await StartSessionUnsafeAsync(new Session(key, engine, null, secrets, settings), ct).ConfigureAwait(false);
    }

    private bool IsActive(string key)
    {
        var snapshot = Snapshot(key);
        return snapshot.CleanupPending || snapshot.Status is ConnectionStatus.Connected or ConnectionStatus.Connecting or ConnectionStatus.Reconnecting;
    }

    private async Task StartSessionUnsafeAsync(Session session, CancellationToken ct)
    {
        session.Mode = session.Settings.Mode;
        session.LocalProxyPort = session.Settings.LocalProxyPort;
        lock (_sessions)
        {
            _sessions[session.Key] = session;
        }

        session.Engine.Log += (_, line) => _log.Info(session.Key, line);
        session.Engine.Traffic += (_, sample) =>
        {
            session.LastTraffic = sample;
            Publish(session);
        };
        session.Engine.StateChanged += (_, e) => OnEngineStateChanged(session, e);

        session.Status = ConnectionStatus.Connecting;
        session.Error = null;
        session.ErrorLocalizationKey = null;
        Publish(session);
        var target = session.Chain is { } chain ? $"chain '{chain.Name}'" : session.Profile is null ? "" : $" for '{session.Profile.Name}'";
        _log.Info(session.Key, $"Starting {session.Engine.Descriptor.DisplayName}{target}");

        try
        {
            await session.Engine.StartAsync(new EngineStartRequest
            {
                Profile = session.Profile,
                Secrets = session.Secrets,
                LocalProxyAuth = session.LocalProxyAuth,
                Chain = session.Hops,
                Group = session.Group, GroupMembers = session.GroupMembers,
                Settings = session.Settings,
                WorkDirectory = Path.Combine(_paths.SessionsDirectory, session.Key),
                RuleSetDirectory = Bydlo.Core.Routing.RoutingPresets.CacheDirectory(_paths.DataDirectory),
            }, ct).ConfigureAwait(false);
            session.Status = ConnectionStatus.Connected;
            session.ConnectedSince = DateTimeOffset.Now;
            session.ReconnectAttempts = 0;
            Publish(session);
        }
        catch (Exception ex)
        {
            session.Status = ConnectionStatus.Error;
            session.Error = _log.Redactor.Redact(ex.Message);
            session.ErrorLocalizationKey = (ex as EngineException)?.LocalizationKey;
            _log.Error(session.Key, session.Error);
            Publish(session);
            try { await session.Engine.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                session.CleanupPending = true;
                session.Error = _log.Redactor.Redact(new AggregateException(ex, cleanupError).Message);
                session.ErrorLocalizationKey = "error.engine.cleanup";
                _log.Error(session.Key, session.Error);
                Publish(session);
                throw new EngineException(session.Error, localizationKey: session.ErrorLocalizationKey);
            }
            throw;
        }
    }

    private async Task StopSessionUnsafeAsync(string key)
    {
        Session? session;
        lock (_sessions)
        {
            _sessions.TryGetValue(key, out session);
        }

        if (session is null)
        {
            return;
        }

        session.UserStopped = true;
        session.Status = ConnectionStatus.Disconnecting;
        Publish(session);
        var errors = new List<Exception>();
        if (session.SystemProxySet)
        {
            try
            {
                await _systemProxy.ClearAsync().ConfigureAwait(false);
                session.SystemProxySet = false;
            }
            catch (Exception ex) { errors.Add(ex); }
        }
        try
        {
            await session.Engine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(key, $"Stop reported: {ex.Message}");
        }
        // Dispose retries engine cleanup even when Stop failed. A successful retry completes it.
        try { await session.Engine.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0)
        {
            session.CleanupPending = true;
            session.Status = ConnectionStatus.Error;
            session.Error = _log.Redactor.Redact(new AggregateException("Session cleanup failed.", errors).Message);
            session.ErrorLocalizationKey = "error.engine.cleanup";
            _log.Error(key, session.Error);
            Publish(session);
            throw new EngineException(session.Error, localizationKey: session.ErrorLocalizationKey);
        }
        lock (_sessions) { _sessions.Remove(key); }
        _log.Info(key, "Stopped");
        SessionChanged?.Invoke(this, Snapshot(key));
    }

    private void OnEngineStateChanged(Session session, EngineStateChangedEventArgs e)
    {
        if (e.State != EngineState.Failed || session.UserStopped || session.Status != ConnectionStatus.Connected)
        {
            return;
        }

        session.Error = e.Error;
        session.ErrorLocalizationKey = null;
        _log.Error(session.Key, e.Error ?? "Engine failed");
        if (session.Settings.AutoReconnect && session.ReconnectAttempts < MaxReconnectAttempts)
        {
            _ = ReconnectAsync(session);
        }
        else
        {
            session.Status = ConnectionStatus.Error;
            Publish(session);
            _ = ClearSystemProxyOnErrorAsync(session);
        }
    }

    private async Task ClearSystemProxyOnErrorAsync(Session session)
    {
        if (!session.SystemProxySet)
        {
            return;
        }

        try
        {
            await _systemProxy.ClearAsync().ConfigureAwait(false);
            session.SystemProxySet = false;
            _log.Info(session.Key, "System proxy cleared after engine failure.");
        }
        catch (Exception ex)
        {
            session.CleanupPending = true;
            session.Error = _log.Redactor.Redact(new AggregateException("System proxy cleanup failed.", ex).Message);
            session.ErrorLocalizationKey = "error.engine.cleanup";
            _log.Error(session.Key, session.Error);
            Publish(session);
        }
    }

    private async Task ReconnectAsync(Session session)
    {
        session.ReconnectAttempts++;
        session.Status = ConnectionStatus.Reconnecting;
        Publish(session);
        var delay = ReconnectDelay(session.ReconnectAttempts);
        _log.Warn(session.Key, $"Reconnecting in {delay.TotalSeconds:0}s (attempt {session.ReconnectAttempts}/{MaxReconnectAttempts})");
        await Task.Delay(delay).ConfigureAwait(false);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (session.UserStopped)
            {
                return;
            }

            session.Mode = session.Settings.Mode;
            session.LocalProxyPort = session.Settings.LocalProxyPort;
            var request = new EngineStartRequest
            {
                Profile = session.Profile,
                Secrets = session.Secrets,
                LocalProxyAuth = session.LocalProxyAuth,
                Chain = session.Hops,
                Group = session.Group, GroupMembers = session.GroupMembers,
                Settings = session.Settings,
                WorkDirectory = Path.Combine(_paths.SessionsDirectory, session.Key),
                RuleSetDirectory = Bydlo.Core.Routing.RoutingPresets.CacheDirectory(_paths.DataDirectory),
            };
            if (session.Engine is IReconnectableEngine retained)
            {
                await retained.ReconnectAsync(request).ConfigureAwait(false);
            }
            else
            {
                await session.Engine.StopAsync().ConfigureAwait(false);
                await session.Engine.StartAsync(request).ConfigureAwait(false);
            }
            session.Status = ConnectionStatus.Connected;
            session.Error = null;
            session.ErrorLocalizationKey = null;
            session.ConnectedSince = DateTimeOffset.Now;
            session.ReconnectAttempts = 0;
            Publish(session);
            _log.Info(session.Key, "Reconnected");
        }
        catch (Exception ex)
        {
            session.Error = _log.Redactor.Redact(ex.Message);
            session.ErrorLocalizationKey = (ex as EngineException)?.LocalizationKey;
            if (session.ReconnectAttempts < MaxReconnectAttempts && session.Settings.AutoReconnect)
            {
                _ = ReconnectAsync(session);
            }
            else
            {
                session.Status = ConnectionStatus.Error;
                Publish(session);
                await ClearSystemProxyOnErrorAsync(session).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fail(string key, Profile? profile, AppSettings settings, string message, string? localizationKey = null)
    {
        _log.Error(key, message);
        SessionChanged?.Invoke(this, new SessionSnapshot(key, ConnectionStatus.Error, profile, null, settings.MockMode, null, message, null)
            { ErrorLocalizationKey = localizationKey });
    }

    private void Publish(Session session) => SessionChanged?.Invoke(this, session.ToSnapshot());

    private sealed class Session(string key, IEngine engine, Profile? profile, ProfileSecrets secrets, AppSettings settings, LocalProxyAuth? lanAuth = null, ProxyChain? chain = null, IReadOnlyList<ChainHop>? hops = null, ServerGroup? group = null, IReadOnlyList<ChainHop>? groupMembers = null)
    {
        public ServerGroup? Group { get; } = group;
        public IReadOnlyList<ChainHop>? GroupMembers { get; } = groupMembers;
        public string Key { get; } = key;

        public IEngine Engine { get; } = engine;

        /// <summary>Exit hop for chain sessions; the connected profile otherwise.</summary>
        public Profile? Profile { get; } = profile;

        public ProfileSecrets Secrets { get; } = secrets;

        public ProxyChain? Chain { get; } = chain;

        public IReadOnlyList<ChainHop>? Hops { get; } = hops;

        public LocalProxyAuth? LocalProxyAuth { get; } = lanAuth;

        public AppSettings Settings { get; } = settings;

        /// <summary>Mode and port captured when the engine was actually started.</summary>
        public ConnectionMode Mode { get; set; } = settings.Mode;

        public int LocalProxyPort { get; set; } = settings.LocalProxyPort;

        public bool IsMock => Engine.Descriptor.Kind == EngineKind.Mock;

        public ConnectionStatus Status { get; set; }

        public string? Error { get; set; }

        public string? ErrorLocalizationKey { get; set; }

        public DateTimeOffset? ConnectedSince { get; set; }

        public TrafficSample? LastTraffic { get; set; }

        public int ReconnectAttempts { get; set; }

        public bool UserStopped { get; set; }

        public bool SystemProxySet { get; set; }

        public bool CleanupPending { get; set; }

        public SessionSnapshot ToSnapshot()
        {
            var selected = Status == ConnectionStatus.Connected && Engine is IServerGroupSource source
                ? GroupMembers?.FirstOrDefault(m => m.Profile.Id == source.SelectedGroupProfileId)?.Profile : null;
            return new(Key, Status, Group is null ? Profile : selected, Engine.Descriptor.Kind, IsMock,
                ConnectedSince, Error, LastTraffic, Mode, LocalProxyPort)
            {
                ErrorLocalizationKey = ErrorLocalizationKey, CleanupPending = CleanupPending, Chain = Chain,
                Group = Group, SelectedGroupProfile = selected,
            };
        }
    }
}
