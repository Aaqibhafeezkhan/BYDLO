using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Core.Engines;

/// <summary>Keeps the local adapter as fallback; a privileged lease is owned by a single IPC connection.</summary>
public sealed class HelperEngine(IEngine local, Func<IPrivilegedOperations> connect, Func<bool> installed) : IEngine, IReconnectableEngine, IActiveConnectionSource, IServerGroupSource
{
    private Guid? _remoteGroupProfileId;
    public Guid? SelectedGroupProfileId => _usingRemote ? _remoteGroupProfileId : (local as IServerGroupSource)?.SelectedGroupProfileId;
    private IPrivilegedOperations? _remote;
    private CancellationTokenSource? _pollCancellation;
    private Task? _poll;
    private EngineState _state;
    private bool _usingRemote;
    public EngineDescriptor Descriptor => local.Descriptor;
    public EngineState State => _usingRemote ? _state : local.State;
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Log;
    public event EventHandler<TrafficSample>? Traffic;

    public bool CanHandle(Profile profile) => local.CanHandle(profile);

    public async Task<IReadOnlyList<ActiveConnection>> ListConnectionsAsync(CancellationToken ct = default) =>
        _remote is not null ? (await _remote.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.ListConnections }, ct).ConfigureAwait(false)).Connections :
            local is IActiveConnectionSource source ? await source.ListConnectionsAsync(ct).ConfigureAwait(false) : [];

    public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
    {
        if (_remote is not null) { await _remote.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.CloseConnection, ConnectionId = id }, ct).ConfigureAwait(false); }
        else if (local is IActiveConnectionSource source) { await source.CloseConnectionAsync(id, ct).ConfigureAwait(false); }
    }

    public async Task ReconnectAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        if (_usingRemote && _remote is null)
        {
            await StartAsync(request, ct).ConfigureAwait(false);
            return;
        }
        if (_remote is not null)
        {
            var reply = await _remote.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Reconnect,
                Start = request with { WorkDirectory = "", RuleSetDirectory = null }, Settings = request.Settings }, ct).ConfigureAwait(false);
            Apply(reply);
            if (reply.Error is not null) { throw new EngineException("helper.error", localizationKey: "helper.error"); }
        }
        else if (local is IReconnectableEngine reconnectable) { await reconnectable.ReconnectAsync(request, ct).ConfigureAwait(false); }
        else { await local.StopAsync(ct).ConfigureAwait(false); await local.StartAsync(request, ct).ConfigureAwait(false); }
    }

    private bool UseHelper(AppSettings settings) => installed() && HelperRequestPolicy.Supports(Descriptor.Kind) &&
        (Descriptor.Kind != EngineKind.SingBox || settings.Mode == ConnectionMode.Tun);

    public async Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!UseHelper(settings)) { return await local.CheckAvailabilityAsync(settings, ct).ConfigureAwait(false); }
        try
        {
            await using var probe = connect();
            var reply = await probe.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Check, Settings = settings }, ct).ConfigureAwait(false);
            return reply.Availability ?? EngineAvailability.NotImplemented(reply.Error ?? "Helper request failed.", "helper.error");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or System.ComponentModel.Win32Exception ||
            ex is OperationCanceledException && !ct.IsCancellationRequested)
        { return EngineAvailability.NotImplemented("The helper is unavailable. Reinstall it or remove it to use elevation.", "helper.error"); }
    }

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        if (!UseHelper(request.Settings))
        {
            local.StateChanged += LocalState;
            local.Log += LocalLog;
            local.Traffic += LocalTraffic;
            await local.StartAsync(request, ct).ConfigureAwait(false);
            return;
        }
        NetworkSafetyGuard.EnsureAllowed("start a privileged engine");
        _usingRemote = true;
        _remote ??= connect();
        var reply = await _remote.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Start,
            Start = request with { WorkDirectory = "", RuleSetDirectory = null }, Settings = request.Settings }, ct).ConfigureAwait(false);
        Apply(reply);
        if (reply.Error is not null) { throw new EngineException("helper.error", localizationKey: "helper.error"); }
        _pollCancellation?.Dispose();
        _pollCancellation = new CancellationTokenSource();
        _poll = PollAsync(_pollCancellation.Token);
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                // Finish an in-flight frame before STOP can reuse this connection.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                Apply(await _remote!.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Poll }, deadline.Token).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            _state = EngineState.Failed;
            StateChanged?.Invoke(this, new(_state, "helper.error"));
            // Closing the lease makes the server stop its engine even when polling failed.
            if (_remote is not null) { await _remote.DisposeAsync().ConfigureAwait(false); _remote = null; }
        }
    }

    private void Apply(HelperReply reply)
    {
        _remoteGroupProfileId = reply.SelectedGroupProfileId;
        foreach (var line in reply.Logs) { Log?.Invoke(this, line); }
        if (reply.Traffic is { } traffic) { Traffic?.Invoke(this, traffic); }
        if (_state != reply.State)
        {
            _state = reply.State;
            StateChanged?.Invoke(this, new(_state, reply.Error is null ? null : "helper.error"));
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _pollCancellation?.Cancel();
        if (_poll is not null) { await _poll.ConfigureAwait(false); }
        if (_usingRemote)
        {
            if (_remote is { } remote)
            {
                var reply = await remote.SendAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Stop }, ct).ConfigureAwait(false);
                if (reply.Error is not null || reply.State != EngineState.Stopped)
                {
                    Apply(new HelperReply { State = EngineState.Failed, Error = "helper.error" });
                    throw new EngineException("Helper did not confirm that the engine stopped.", localizationKey: "error.engine.cleanup");
                }
                Apply(reply);
                await remote.DisposeAsync().ConfigureAwait(false);
                _remote = null;
            }
            else if (_state != EngineState.Stopped)
            {
                throw new EngineException("Helper disconnected before confirming that the engine stopped.", localizationKey: "error.engine.cleanup");
            }
        }
        await local.StopAsync(ct).ConfigureAwait(false);
    }

    private void LocalState(object? sender, EngineStateChangedEventArgs args) => StateChanged?.Invoke(this, args);
    private void LocalLog(object? sender, string line) => Log?.Invoke(this, line);
    private void LocalTraffic(object? sender, TrafficSample traffic) => Traffic?.Invoke(this, traffic);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _pollCancellation?.Dispose();
        local.StateChanged -= LocalState;
        local.Log -= LocalLog;
        local.Traffic -= LocalTraffic;
        await local.DisposeAsync().ConfigureAwait(false);
    }
}
