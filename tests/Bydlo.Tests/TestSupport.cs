using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Engines;
using Bydlo.Engines.Installation;

namespace Bydlo.Tests;

public sealed class FakeExitEvents : IExitEventFactory, IExitEvent
{
    public string? Name { get; private set; }
    public bool Signaled { get; private set; }
    public bool Disposed { get; private set; }
    public IExitEvent Create(string name) { Name = name; return this; }
    public void Signal() => Signaled = true;
    public void Dispose() => Disposed = true;
}

/// <summary>Records process specs and lets tests script output. Never starts anything.</summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    public List<ProcessSpec> Started { get; } = [];

    public List<ProcessSpec> Completed { get; } = [];

    /// <summary>Lines emitted right after start.</summary>
    public List<string> StartupOutput { get; } = [];

    public int CompletionExitCode { get; set; }

    public Func<ProcessSpec, (int ExitCode, string Output)>? OnCompletion { get; set; }

    public Action<ProcessSpec>? OnStart { get; set; }

    public FakeProcess? Last { get; private set; }

    public IManagedProcess Start(ProcessSpec spec)
    {
        Started.Add(spec);
        OnStart?.Invoke(spec);
        Last = new FakeProcess();
        var p = Last;
        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            foreach (var line in StartupOutput)
            {
                p.Emit(line);
            }
        });
        return p;
    }

    public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default)
    {
        Completed.Add(spec);
        return Task.FromResult(OnCompletion?.Invoke(spec) ?? (CompletionExitCode, "ok"));
    }
}

public sealed class FakeProcess : IManagedProcess
{
    public int Id => 4242;

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;

    public bool HasExited { get; private set; }

    public int? ExitCode { get; private set; }

    public bool Stopped { get; private set; }

    public Exception? StopError { get; set; }

    public int StopFailuresRemaining { get; set; }

    public event EventHandler<string>? OutputLine;

    public event EventHandler<int>? Exited;

    public void Emit(string line) => OutputLine?.Invoke(this, line);

    public void Crash(int code = 1)
    {
        HasExited = true;
        ExitCode = code;
        Exited?.Invoke(this, code);
    }

    public Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (StopFailuresRemaining > 0)
        {
            StopFailuresRemaining--;
            throw new IOException("Transient fake process stop failure.");
        }
        if (StopError is { } error) { throw error; }
        Stopped = true;
        HasExited = true;
        ExitCode = 0;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class FakePrivileges(bool elevated) : IPrivilegeService
{
    public bool IsElevated => elevated;

    public bool CanRestartElevated => false;

    public bool TryRestartElevated(IReadOnlyList<string> args) => false;
}

public sealed class FakePorts(params int[] busy) : IPortChecker
{
    public bool IsTcpPortInUse(int port) => busy.Contains(port);
}

/// <summary>Locator that pretends an engine binary exists at a fake path.</summary>
public sealed class FakeLocator(PlatformInfo platform, string? path = "/fake/engine")
    : EngineLocator(new EngineInstaller(new HttpClient(), EngineLock.LoadEmbedded(), TestDirs.New("engines")), platform)
{
    public override string? Locate(EngineKind kind, AppSettings settings) => path;
}

public static class TestDirs
{
    /// <summary>Fresh temp directory (TMP is redirected into the checkout by scripts/env.*).</summary>
    public static string New(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "bydlo-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
