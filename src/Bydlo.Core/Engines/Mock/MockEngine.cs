using Bydlo.Core.Models;

namespace Bydlo.Core.Engines.Mock;

/// <summary>
/// TEST TOOL. Simulates connect / disconnect / traffic / errors without starting any process or touching the network.
/// Never presented to users as a working connection: the UI shows a "MOCK" badge whenever it is used.
/// </summary>
public sealed class MockEngine : IEngine
{
    public static readonly EngineDescriptor MockDescriptor = new()
    {
        Kind = EngineKind.Mock,
        DisplayName = "Mock engine (simulation)",
        Upstream = "built-in",
        License = "GPL-3.0-or-later",
        Protocols = Enum.GetValues<ProtocolKind>().ToHashSet(),
        Platforms = Enum.GetValues<OsFamily>().ToDictionary(o => o, _ => SupportLevel.Implemented),
    };

    private readonly TimeProvider _time;
    private readonly Random _random;
    private CancellationTokenSource? _loop;
    private long _totalUp;
    private long _totalDown;

    public MockEngine(TimeProvider? time = null, int? seed = null, EngineDescriptor? descriptor = null)
    {
        _time = time ?? TimeProvider.System;
        _random = seed is { } s ? new Random(s) : new Random();
        Descriptor = descriptor ?? MockDescriptor;
    }

    public EngineDescriptor Descriptor { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;

    /// <summary>Speeds tests up; 1.0 = realistic timings.</summary>
    public double TimeScale { get; set; } = 1.0;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? Log;

    public event EventHandler<TrafficSample>? Traffic;

    public bool CanHandle(Profile profile) => true;

    public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
        Task.FromResult(EngineAvailability.Ready("mock"));

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        // The mock simulates end-to-end traffic, so chains are structurally validated like everywhere else.
        if (request.Chain is { } chain && chain.Count < ChainValidator.MinHops)
        {
            throw new EngineException("A chain needs at least two servers.", localizationKey: "error.chain.toofew");
        }

        var scenario = request.Settings.MockScenario;
        Set(EngineState.Starting);
        Log?.Invoke(this, $"[mock] starting simulated session for '{request.Profile?.Name ?? Descriptor.DisplayName}' (scenario: {scenario})");

        var delay = scenario == MockScenario.SlowConnect ? 6000 : 1200;
        await Task.Delay(Scale(delay), _time, ct).ConfigureAwait(false);
        Log?.Invoke(this, "[mock] handshake...");

        if (scenario == MockScenario.FailOnConnect)
        {
            const string error = "Simulated failure: TLS handshake timeout (mock)";
            Log?.Invoke(this, "[mock] " + error);
            Set(EngineState.Failed, error);
            throw new EngineException(error);
        }

        Set(EngineState.Running);
        Log?.Invoke(this, "[mock] simulated tunnel is up — no real traffic is carried");
        _loop = new CancellationTokenSource();
        _ = RunTrafficLoopAsync(scenario, _loop.Token);
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Set(EngineState.Stopping);
        _loop?.Cancel();
        _loop = null;
        Log?.Invoke(this, "[mock] stopped");
        Set(EngineState.Stopped);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _loop?.Cancel();
        return ValueTask.CompletedTask;
    }

    private async Task RunTrafficLoopAsync(MockScenario scenario, CancellationToken ct)
    {
        var started = _time.GetUtcNow();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Scale(1000), _time, ct).ConfigureAwait(false);
                var up = (long)(_random.NextDouble() * 180_000) + 4_000;
                var down = (long)(_random.NextDouble() * 2_400_000) + 30_000;
                _totalUp += up;
                _totalDown += down;
                Traffic?.Invoke(this, new TrafficSample(_time.GetUtcNow(), up, down, _totalUp, _totalDown));

                if (scenario == MockScenario.DropAfterConnect && _time.GetUtcNow() - started > Scale(20_000))
                {
                    const string error = "Simulated crash: engine process exited with code 1 (mock)";
                    Log?.Invoke(this, "[mock] " + error);
                    Set(EngineState.Failed, error);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private TimeSpan Scale(int ms) => TimeSpan.FromMilliseconds(Math.Max(1, ms * TimeScale));

    private void Set(EngineState state, string? error = null)
    {
        State = state;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
    }
}
