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
    TrafficSample? LastTraffic);

/// <summary>
/// The single entry point for starting and stopping anything that carries traffic.
/// Owns one primary tunnel plus independent tool sessions (Zapret, TgWsProxy), checks conflicts,
/// supervises engines and reconnects after crashes.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    public const string PrimaryKey = "primary";
    public const string ZapretKey = "zapret";
    public const string TgWsProxyKey = "tgwsproxy";
    public const string TgWsProxySecretId = "tool:tgwsproxy";

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

    public SessionSnapshot Primary => Snapshot(PrimaryKey);

    public SessionSnapshot Zapret => Snapshot(ZapretKey);

    public SessionSnapshot TgWsProxy => Snapshot(TgWsProxyKey);

    public SessionSnapshot Snapshot(string key)
    {
        lock (_sessions)
        {
            return _sessions.TryGetValue(key, out var s) ? s.ToSnapshot() : new SessionSnapshot(key, ConnectionStatus.Disconnected, null, null, false, null, null, null);
        }
    }

    public IReadOnlyList<ConflictIssue> PreviewConflicts(Profile profile, AppSettings settings) =>
        _conflicts.CheckPrimary(profile, settings, IsActive(ZapretKey), IsActive(TgWsProxyKey));

    /// <summary>Connects the primary tunnel. Disconnects a previous primary first.</summary>
    public async Task ConnectAsync(Profile profile, AppSettings settings, CancellationToken ct = default)
    {
        var blocking = PreviewConflicts(profile, settings).Where(i => i.Severity == ConflictSeverity.Blocking).ToList();
        if (blocking.Count > 0)
        {
            Fail(PrimaryKey, profile, settings, string.Join(" ", blocking.Select(b => b.Message)));
            throw new EngineException(blocking[0].Message);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopSessionUnsafeAsync(PrimaryKey).ConfigureAwait(false);
            var engine = _registry.CreateFor(profile, settings, _os);
            var secrets = await _secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false);
            var session = new Session(PrimaryKey, engine, profile, secrets, settings);
            await StartSessionUnsafeAsync(session, ct).ConfigureAwait(false);

            if (settings.SetSystemProxy && settings.Mode == ConnectionMode.LocalProxy && !session.IsMock && _systemProxy.IsSupported)
            {
                NetworkSafetyGuard.EnsureAllowed("set the system proxy");
                await _systemProxy.SetAsync("127.0.0.1", settings.LocalProxyPort, ct).ConfigureAwait(false);
                session.SystemProxySet = true;
            }
        }
        finally
        {
            _gate.Release();
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

    public Task StartZapretAsync(AppSettings settings, CancellationToken ct = default) =>
        StartToolAsync(ZapretKey, EngineKind.Zapret, settings, _conflicts.CheckZapret(settings, IsActive(PrimaryKey)), ct);

    public Task StartTgWsProxyAsync(AppSettings settings, CancellationToken ct = default) =>
        StartToolAsync(TgWsProxyKey, EngineKind.TgWsProxy, settings, _conflicts.CheckTgWsProxy(settings, IsActive(PrimaryKey)), ct);

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
            foreach (var key in new[] { PrimaryKey, ZapretKey, TgWsProxyKey })
            {
                await StopSessionUnsafeAsync(key).ConfigureAwait(false);
            }
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

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopSessionUnsafeAsync(key).ConfigureAwait(false);
            var engine = _registry.Create(kind, settings);
            var secrets = await _secrets.LoadAsync(key == TgWsProxyKey ? TgWsProxySecretId : key, ct).ConfigureAwait(false);
            await StartSessionUnsafeAsync(new Session(key, engine, null, secrets, settings), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsActive(string key) => Snapshot(key).Status is ConnectionStatus.Connected or ConnectionStatus.Connecting or ConnectionStatus.Reconnecting;

    private async Task StartSessionUnsafeAsync(Session session, CancellationToken ct)
    {
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
        Publish(session);
        _log.Info(session.Key, $"Starting {session.Engine.Descriptor.DisplayName}{(session.Profile is null ? "" : $" for '{session.Profile.Name}'")}");

        try
        {
            await session.Engine.StartAsync(new EngineStartRequest
            {
                Profile = session.Profile,
                Secrets = session.Secrets,
                Settings = session.Settings,
                WorkDirectory = Path.Combine(_paths.SessionsDirectory, session.Key),
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
            _log.Error(session.Key, session.Error);
            Publish(session);
            await session.Engine.DisposeAsync().ConfigureAwait(false);
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
        try
        {
            if (session.SystemProxySet)
            {
                await _systemProxy.ClearAsync().ConfigureAwait(false);
            }

            await session.Engine.StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn(key, $"Stop reported: {ex.Message}");
        }
        finally
        {
            await session.Engine.DisposeAsync().ConfigureAwait(false);
            lock (_sessions)
            {
                _sessions.Remove(key);
            }

            _log.Info(key, "Stopped");
            SessionChanged?.Invoke(this, Snapshot(key));
        }
    }

    private void OnEngineStateChanged(Session session, EngineStateChangedEventArgs e)
    {
        if (e.State != EngineState.Failed || session.UserStopped || session.Status != ConnectionStatus.Connected)
        {
            return;
        }

        session.Error = e.Error;
        _log.Error(session.Key, e.Error ?? "Engine failed");
        if (session.Settings.AutoReconnect && session.ReconnectAttempts < MaxReconnectAttempts)
        {
            _ = ReconnectAsync(session);
        }
        else
        {
            session.Status = ConnectionStatus.Error;
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

            await session.Engine.StopAsync().ConfigureAwait(false);
            await session.Engine.StartAsync(new EngineStartRequest
            {
                Profile = session.Profile,
                Secrets = session.Secrets,
                Settings = session.Settings,
                WorkDirectory = Path.Combine(_paths.SessionsDirectory, session.Key),
            }).ConfigureAwait(false);
            session.Status = ConnectionStatus.Connected;
            session.Error = null;
            session.ConnectedSince = DateTimeOffset.Now;
            session.ReconnectAttempts = 0;
            Publish(session);
            _log.Info(session.Key, "Reconnected");
        }
        catch (Exception ex)
        {
            session.Error = _log.Redactor.Redact(ex.Message);
            if (session.ReconnectAttempts < MaxReconnectAttempts && session.Settings.AutoReconnect)
            {
                _ = ReconnectAsync(session);
            }
            else
            {
                session.Status = ConnectionStatus.Error;
                Publish(session);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Fail(string key, Profile? profile, AppSettings settings, string message)
    {
        _log.Error(key, message);
        SessionChanged?.Invoke(this, new SessionSnapshot(key, ConnectionStatus.Error, profile, null, settings.MockMode, null, message, null));
    }

    private void Publish(Session session) => SessionChanged?.Invoke(this, session.ToSnapshot());

    private sealed class Session(string key, IEngine engine, Profile? profile, ProfileSecrets secrets, AppSettings settings)
    {
        public string Key { get; } = key;

        public IEngine Engine { get; } = engine;

        public Profile? Profile { get; } = profile;

        public ProfileSecrets Secrets { get; } = secrets;

        public AppSettings Settings { get; } = settings;

        public bool IsMock => Engine.Descriptor.Kind == EngineKind.Mock;

        public ConnectionStatus Status { get; set; }

        public string? Error { get; set; }

        public DateTimeOffset? ConnectedSince { get; set; }

        public TrafficSample? LastTraffic { get; set; }

        public int ReconnectAttempts { get; set; }

        public bool UserStopped { get; set; }

        public bool SystemProxySet { get; set; }

        public SessionSnapshot ToSnapshot() =>
            new(Key, Status, Profile, Engine.Descriptor.Kind, IsMock, ConnectedSince, Error, LastTraffic);
    }
}
