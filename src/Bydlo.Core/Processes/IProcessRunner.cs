namespace Bydlo.Core.Processes;

public sealed record ProcessSpec
{
    public required string FileName { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Short name used in logs and the process journal (e.g. "sing-box").</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>True for processes that touch the network (subject to <see cref="Security.NetworkSafetyGuard"/>).</summary>
    public bool AffectsNetwork { get; init; } = true;

    /// <summary>Request SIGTERM on Unix before falling back to forced termination.</summary>
    public bool GracefulTermination { get; init; }

    /// <summary>OpenVPN's documented Windows --service exit event; no system service is installed.</summary>
    public string? WindowsExitEventName { get; init; }
}

/// <summary>A started child process.</summary>
public interface IManagedProcess : IAsyncDisposable
{
    int Id { get; }

    DateTimeOffset StartedAt { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    event EventHandler<string>? OutputLine;

    event EventHandler<int>? Exited;

    /// <summary>
    /// Starts raising <see cref="OutputLine"/> and <see cref="Exited"/>. Until then early output
    /// stays queued (in the OS pipe for real processes) and an early exit is reported on this call,
    /// so a consumer that subscribes first never misses a one-shot ready line (issue #468).
    /// Idempotent; nothing is delivered twice.
    /// </summary>
    void BeginEventDelivery();

    /// <summary>Stops the process tree. Waits up to <paramref name="timeout"/> before killing.</summary>
    Task StopAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Abstraction over process creation so engines can be tested without spawning anything.</summary>
public interface IProcessRunner
{
    /// <summary>Starts the process; subscribe to its events, then call <see cref="IManagedProcess.BeginEventDelivery"/>.</summary>
    IManagedProcess Start(ProcessSpec spec);

    /// <summary>Runs a short-lived helper (e.g. <c>sing-box check</c>) and returns exit code + output.</summary>
    Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default);
}
