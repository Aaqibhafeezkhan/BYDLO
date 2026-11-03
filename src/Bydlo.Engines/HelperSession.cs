using System.Collections.Concurrent;
using Bydlo.Core.Engines;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Engines;

/// <summary>One authenticated connection, one engine, no cross-client process control.</summary>
public sealed class HelperSession(Func<Core.Models.EngineKind, IEngine> createEngine, string sessionRoot, HelperLeasePool? leases = null) : IAsyncDisposable
{
    private IDisposable? _lease;
    private IEngine? _engine;
    private Core.Models.EngineKind? _kind;
    private string? _workDirectory;
    private bool _stopped = true;
    private readonly ConcurrentQueue<string> _logs = new();
    private TrafficSample? _traffic;

    public async Task<HelperReply> DispatchAsync(HelperRequest request, CancellationToken ct)
    {
        HelperRequestPolicy.Validate(request);
        if (_kind is { } kind && kind != request.Engine) { throw new InvalidDataException("Engine lease cannot change kind."); }
        _kind = request.Engine;
        if (_engine is null)
        {
            _engine = createEngine(request.Engine);
            _engine.Log += OnLog;
            _engine.Traffic += OnTraffic;
        }
        switch (request.Operation)
        {
            case HelperOperation.Check:
                return new HelperReply { Availability = await _engine.CheckAvailabilityAsync(request.Settings, ct).ConfigureAwait(false), State = _engine.State };
            case HelperOperation.Start:
                if (_workDirectory is not null) { throw new InvalidDataException("A session can start only once."); }
                _lease = leases?.Acquire(request.Engine);
                _stopped = false;
                _workDirectory = Path.Combine(sessionRoot, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_workDirectory);
                await _engine.StartAsync(request.Start! with { WorkDirectory = _workDirectory, RuleSetDirectory = null }, ct).ConfigureAwait(false);
                break;
            case HelperOperation.Stop:
                await _engine.StopAsync(ct).ConfigureAwait(false);
                _stopped = true;
                _lease?.Dispose();
                _lease = null;
                break;
            case HelperOperation.Reconnect:
                if (_workDirectory is null || _stopped) { throw new InvalidDataException("An active lease is required before reconnecting."); }
                var restart = request.Start! with { WorkDirectory = _workDirectory, RuleSetDirectory = null };
                if (_engine is IReconnectableEngine reconnectable)
                { await reconnectable.ReconnectAsync(restart, ct).ConfigureAwait(false); }
                else
                {
                    await _engine.StopAsync(ct).ConfigureAwait(false);
                    await _engine.StartAsync(restart, ct).ConfigureAwait(false);
                }
                break;
            case HelperOperation.ListConnections:
                return new HelperReply { State = _engine.State, Connections = _engine is IActiveConnectionSource source
                    ? [.. (await source.ListConnectionsAsync(ct).ConfigureAwait(false)).Take(1000)] : [] };
            case HelperOperation.CloseConnection:
                if (_engine is IActiveConnectionSource active) { await active.CloseConnectionAsync(request.ConnectionId!, ct).ConfigureAwait(false); }
                break;
            case HelperOperation.Poll:
                break;
        }
        var logs = new List<string>();
        while (_logs.TryDequeue(out var line)) { logs.Add(line); }
        return new HelperReply { State = _engine.State, Logs = [.. logs], Traffic = Interlocked.Exchange(ref _traffic, null),
            SelectedGroupProfileId = (_engine as IServerGroupSource)?.SelectedGroupProfileId };
    }

    private void OnLog(object? sender, string line)
    {
        // Engine output is already redacted. Bound both individual lines and queued output.
        if (_logs.Count < 64) { _logs.Enqueue(line.Length > 4096 ? line[..4096] : line); }
    }
    private void OnTraffic(object? sender, TrafficSample traffic) => _traffic = traffic;

    public async ValueTask DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
        if (_workDirectory is not null && Directory.Exists(_workDirectory)) { Directory.Delete(_workDirectory, true); }
        _lease?.Dispose();
        _lease = null;
    }
}
