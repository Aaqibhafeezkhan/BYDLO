using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Core.Engines;

/// <summary>
/// Base class for engines that wrap one external process: start, watch output, detect readiness,
/// report unexpected exits, stop the process tree.
/// </summary>
public abstract class ProcessEngineBase(IProcessRunner runner, LogRedactor redactor) : IEngine
{
    private IManagedProcess? _process;
    private TaskCompletionSource? _ready;
    private bool _stopping;

    public abstract EngineDescriptor Descriptor { get; }

    public EngineState State { get; private set; } = EngineState.Stopped;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? Log;

    public event EventHandler<TrafficSample>? Traffic;

    protected IProcessRunner Runner => runner;

    protected LogRedactor Redactor => redactor;

    /// <summary>How long to wait for <see cref="IsReadyLine"/> before assuming the process is up.</summary>
    protected virtual TimeSpan ReadyTimeout => TimeSpan.FromSeconds(8);

    /// <summary>When true, absence of a ready line within <see cref="ReadyTimeout"/> while still alive counts as started.</summary>
    protected virtual bool AliveMeansReady => true;

    public abstract bool CanHandle(Profile profile);

    public abstract Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Builds the process to launch (write configs into <see cref="EngineStartRequest.WorkDirectory"/>).</summary>
    protected abstract Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct);

    /// <summary>Line that proves the engine is serving traffic.</summary>
    protected virtual bool IsReadyLine(string line) => false;

    /// <summary>Line that proves start-up failed; returns a user-friendly message.</summary>
    protected virtual string? TryParseFatal(string line) =>
        line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? line : null;

    protected virtual Task OnStartedAsync(EngineStartRequest request, CancellationToken ct) => Task.CompletedTask;

    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

    protected void EmitTraffic(TrafficSample sample) => Traffic?.Invoke(this, sample);

    protected void EmitLog(string line) => Log?.Invoke(this, redactor.Redact(line));

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        if (State is EngineState.Running or EngineState.Starting)
        {
            throw new EngineException($"{Descriptor.DisplayName} is already running.");
        }

        SetState(EngineState.Starting);
        try
        {
            var availability = await CheckAvailabilityAsync(request.Settings, ct).ConfigureAwait(false);
            if (!availability.IsReady)
            {
                throw new EngineException(availability.Message);
            }

            foreach (var secret in request.Secrets.Values.Values)
            {
                redactor.RegisterSecret(secret);
            }

            Directory.CreateDirectory(request.WorkDirectory);
            var spec = await PrepareAsync(request, availability, ct).ConfigureAwait(false);
            _stopping = false;
            _ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? fatal = null;

            _process = runner.Start(spec);
            _process.OutputLine += (_, line) =>
            {
                EmitLog(line);
                if (IsReadyLine(line))
                {
                    _ready.TrySetResult();
                }
                else if (TryParseFatal(line) is { } f)
                {
                    fatal ??= redactor.Redact(f);
                }
            };
            _process.Exited += (_, code) => OnProcessExited(code, fatal);

            var timeout = Task.Delay(ReadyTimeout, ct);
            var finished = await Task.WhenAny(_ready.Task, timeout).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                throw new EngineException(fatal ?? $"{Descriptor.DisplayName} exited with code {_process.ExitCode}.");
            }

            if (finished == timeout && !AliveMeansReady)
            {
                throw new EngineException($"{Descriptor.DisplayName} did not report readiness in {ReadyTimeout.TotalSeconds:0}s.");
            }

            await OnStartedAsync(request, ct).ConfigureAwait(false);
            SetState(EngineState.Running);
        }
        catch (Exception ex)
        {
            await KillQuietlyAsync().ConfigureAwait(false);
            SetState(EngineState.Failed, ex.Message);
            throw ex as EngineException ?? new EngineException(ex.Message, ex);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_process is null)
        {
            SetState(EngineState.Stopped);
            return;
        }

        SetState(EngineState.Stopping);
        _stopping = true;
        await OnStoppingAsync().ConfigureAwait(false);
        await _process.StopAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        _process = null;
        SetState(EngineState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await KillQuietlyAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnProcessExited(int code, string? fatal)
    {
        _ready?.TrySetResult();
        if (_stopping || State is EngineState.Stopped or EngineState.Starting)
        {
            return;
        }

        SetState(EngineState.Failed, fatal ?? $"{Descriptor.DisplayName} exited unexpectedly (code {code}).");
    }

    private async Task KillQuietlyAsync()
    {
        if (_process is null)
        {
            return;
        }

        _stopping = true;
        try
        {
            await _process.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
        }

        _process = null;
    }

    protected void SetState(EngineState state, string? error = null)
    {
        State = state;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
    }
}
