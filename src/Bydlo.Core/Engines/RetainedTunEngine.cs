using System.Text.Json;
using Bydlo.Core.Models;

namespace Bydlo.Core.Engines;

/// <summary>Keeps the strict TUN alive across transport failures and reconnect attempts.</summary>
public sealed class RetainedTunEngine : IEngine, IReconnectableEngine
{
    public const string GuardProcessName = "BYDLO kill switch";
    private readonly IEngine _backend;
    private readonly Func<IEngine> _guardFactory;
    private IEngine? _guard;
    private EngineStartRequest? _backendRequest;
    private bool _stopping;

    public RetainedTunEngine(IEngine backend, Func<IEngine> guardFactory)
    {
        _backend = backend;
        _guardFactory = guardFactory;
        backend.Log += (_, line) => Log?.Invoke(this, line);
        backend.Traffic += (_, sample) => Traffic?.Invoke(this, sample);
        backend.StateChanged += (_, e) => Publish(e);
    }

    public EngineDescriptor Descriptor => _backend.Descriptor;
    public EngineState State { get; private set; }
    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Log;
    public event EventHandler<TrafficSample>? Traffic;

    public bool CanHandle(Profile profile) => _backend.CanHandle(profile);
    public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
        _backend.CheckAvailabilityAsync(settings, ct);

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        _stopping = false;
        if (_guard is not null) { throw new EngineException("error.killswitch.active"); }
        // Edits in the UI apply to the next connection, including when reconnecting a proxy session.
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(request.Settings))!;
        _backendRequest = request with { Settings = settings };
        if (!settings.KillSwitch || settings.Mode != ConnectionMode.Tun)
        {
            await _backend.StartAsync(_backendRequest, ct).ConfigureAwait(false);
            return;
        }

        // Snapshot settings: edits in the UI must not change the active protection or backend port.
        var backendSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        backendSettings.Mode = ConnectionMode.LocalProxy;
        backendSettings.KillSwitch = false;
        backendSettings.SetSystemProxy = false;
        backendSettings.Routing = new RoutingSettings
        {
            BypassPrivateNetworks = false,
            PreserveMeshVpnRanges = false,
            RemoteDns = settings.Routing.RemoteDns,
            DirectDns = settings.Routing.DirectDns,
        };
        _backendRequest = request with { Settings = backendSettings, WorkDirectory = Path.Combine(request.WorkDirectory, "transport") };
        _guard = _guardFactory();
        _guard.Log += (_, line) => Log?.Invoke(this, line);
        _guard.StateChanged += (_, e) =>
        {
            if (!_stopping && e.State == EngineState.Failed) { Publish(e); }
        };
        try
        {
            await _guard.StartAsync(request with { Settings = settings, Secrets = ProfileSecrets.Empty,
                WorkDirectory = Path.Combine(request.WorkDirectory, "kill-switch") }, ct).ConfigureAwait(false);
            await _backend.StartAsync(_backendRequest, ct).ConfigureAwait(false);
            if (_guard.State != EngineState.Running) { throw new EngineException("error.killswitch.tun"); }
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ReconnectAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        if (_guard is not null && _guard.State != EngineState.Running)
        {
            throw new EngineException("error.killswitch.tun");
        }
        await _backend.StopAsync(ct).ConfigureAwait(false);
        await _backend.StartAsync(_backendRequest ?? request, ct).ConfigureAwait(false);
        if (_guard is not null && _guard.State != EngineState.Running)
        {
            throw new EngineException("error.killswitch.tun");
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _stopping = true;
        try { await _backend.StopAsync(ct).ConfigureAwait(false); }
        finally
        {
            // Release protection even when transport cleanup fails or cancellation was requested.
            if (_guard is { } guard)
            {
                try { await guard.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                finally
                {
                    await guard.DisposeAsync().ConfigureAwait(false);
                    _guard = null;
                }
            }
            _backendRequest = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            await _backend.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private void Publish(EngineStateChangedEventArgs e)
    {
        State = e.State;
        StateChanged?.Invoke(this, e);
    }
}
