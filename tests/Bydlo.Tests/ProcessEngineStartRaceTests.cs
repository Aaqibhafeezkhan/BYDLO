using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue 340: exit on the start boundary must not publish Running.</summary>
public sealed class ProcessEngineStartRaceTests
{
    private static EngineStartRequest Request() => new()
    {
        Settings = new AppSettings(),
        WorkDirectory = TestDirs.New("start-race"),
    };

    [Fact]
    public async Task Exit_between_final_check_and_running_reports_failure()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: 2, exitCode: 1);
        var engine = new RaceEngine(runner);
        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Contains("exited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.True(runner.Process!.HasExited);
    }

    [Fact]
    public async Task Exit_before_first_check_reports_failure()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: 1, exitCode: 3);
        var engine = new RaceEngine(runner);
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Equal(EngineState.Failed, engine.State);
    }

    [Fact]
    public async Task Clean_start_still_reaches_running()
    {
        var runner = new BoundaryExitRunner(fireOnReadNumber: int.MaxValue, exitCode: 1);
        var engine = new RaceEngine(runner);
        await engine.StartAsync(Request());
        Assert.Equal(EngineState.Running, engine.State);
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
    }

    [Fact]
    public async Task Unexpected_exit_after_running_still_fails_with_cleanup()
    {
        var runner = new FakeProcessRunner();
        var engine = new RaceEngine(runner);
        await engine.StartAsync(Request());
        Assert.Equal(EngineState.Running, engine.State);
        runner.Last!.Crash(2);
        await Task.Delay(200);
        Assert.Equal(EngineState.Failed, engine.State);
    }

    private sealed class RaceEngine(IProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor())
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.SingBox,
            DisplayName = "Fake race engine",
            License = "",
            Upstream = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };
        public override bool CanHandle(Profile profile) => true;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake", Arguments = [] });
    }

    /// <summary>
    /// Returns false on the configured HasExited read while firing Exited synchronously,
    /// exactly like the issue repro. Later reads report the real exited state.
    /// </summary>
    private sealed class BoundaryExitRunner(int fireOnReadNumber, int exitCode) : IProcessRunner
    {
        public BoundaryExitProcess? Process { get; private set; }

        public IManagedProcess Start(ProcessSpec spec)
        {
            Process = new BoundaryExitProcess(fireOnReadNumber, exitCode);
            return Process;
        }

        public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult((0, "ok"));
    }

    private sealed class BoundaryExitProcess(int fireOnReadNumber, int exitCode) : IManagedProcess
    {
        private int _reads;
        private bool _exited;

        public int Id => 4242;
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
        public int? ExitCode { get; private set; }

        public bool HasExited
        {
            get
            {
                _reads++;
                if (_reads == fireOnReadNumber && !_exited)
                {
                    _exited = true;
                    ExitCode = exitCode;
                    Exited?.Invoke(this, exitCode);
                    return false;
                }
                return _exited;
            }
        }

#pragma warning disable CS0067
        public event EventHandler<string>? OutputLine;
#pragma warning restore CS0067
        public event EventHandler<int>? Exited;

        public Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            _exited = true;
            ExitCode ??= 0;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
