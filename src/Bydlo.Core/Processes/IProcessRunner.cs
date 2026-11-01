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

    /// <summary>Stops the process tree. Waits up to <paramref name="timeout"/> before killing.</summary>
    Task StopAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Abstraction over process creation so engines can be tested without spawning anything.</summary>
public interface IProcessRunner
{
    IManagedProcess Start(ProcessSpec spec);

    /// <summary>Runs a short-lived helper (e.g. <c>sing-box check</c>) and returns exit code + output.</summary>
    Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default);
}
