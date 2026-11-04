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
    private readonly SemaphoreSlim _remoteGate = new(1);
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
        _usingRemote ? (await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.ListConnections }, ct, finishFrame: true).ConfigureAwait(false)).Connections :
            local is IActiveConnectionSource source ? await source.ListConnectionsAsync(ct).ConfigureAwait(false) : [];

    public async Task CloseConnectionAsync(string id, CancellationToken ct = default)
    {
        if (_usingRemote) { await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.CloseConnection, ConnectionId = id }, ct, finishFrame: true).ConfigureAwait(false); }
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
            var reply = await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Reconnect,
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
            _usingRemote = false;
            local.StateChanged += LocalState;
            local.Log += LocalLog;
            local.Traffic += LocalTraffic;
            await local.StartAsync(request, ct).ConfigureAwait(false);
            return;
        }
        NetworkSafetyGuard.EnsureAllowed("start a privileged engine");
        _usingRemote = true;
        _remote ??= connect();
        var reply = await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Start,
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
                Apply(await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Poll }, ct, finishFrame: true).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (IsTransportFailure(ex) || ex is EngineException && _remote is null) { }
    }

    private static bool IsTransportFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException or ObjectDisposedException
            or System.ComponentModel.Win32Exception or System.Text.Json.JsonException;

    private async Task<HelperReply> SendRemoteAsync(HelperRequest request, CancellationToken ct, bool finishFrame = false)
    {
        var remote = _remote;
        await _remoteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (remote is null || !ReferenceEquals(remote, _remote))
            {
                throw new EngineException("helper.error", localizationKey: "helper.error");
            }

            ct.ThrowIfCancellationRequested();
            // A page or the poll loop can cancel while queued. Once a frame starts, let it finish
            // within its own deadline so a canceled read does not close the privileged lease.
            using var deadline = finishFrame ? new CancellationTokenSource(TimeSpan.FromSeconds(5)) : null;
            HelperReply reply;
            try { reply = await remote.SendAsync(request, deadline?.Token ?? ct).ConfigureAwait(false); }
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                _remote = null;
                try { await remote.DisposeAsync().ConfigureAwait(false); }
                finally { Apply(new HelperReply { State = EngineState.Failed, Error = "helper.error" }); }
                throw;
            }

            if (request.Operation == HelperOperation.Stop && reply.State == EngineState.Stopped && reply.Error is null)
            {
                _remote = null;
                await remote.DisposeAsync().ConfigureAwait(false);
            }

            if (finishFrame) { ct.ThrowIfCancellationRequested(); }
            return reply;
        }
        finally { _remoteGate.Release(); }
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
            if (_remote is not null)
            {
                try
                {
                    var reply = await SendRemoteAsync(new HelperRequest { Engine = Descriptor.Kind, Operation = HelperOperation.Stop }, ct).ConfigureAwait(false);
                    if (reply.Error is not null || reply.State != EngineState.Stopped)
                    {
                        Apply(new HelperReply { State = EngineState.Failed, Error = "helper.error" });
                        throw new EngineException("Helper did not confirm that the engine stopped.", localizationKey: "error.engine.cleanup");
                    }
                }
                catch (Exception ex) when (_remote is null && (IsTransportFailure(ex) || ex is EngineException) && !ct.IsCancellationRequested) { }
            }

            // A dead transport cannot be reused. The helper disposes the engine when its lease closes.
            Apply(new HelperReply { State = EngineState.Stopped });
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
