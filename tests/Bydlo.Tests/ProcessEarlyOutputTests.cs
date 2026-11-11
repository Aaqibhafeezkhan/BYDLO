using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>
/// Issue 468: a one-shot ready line printed before <see cref="IProcessRunner.Start"/> returns
/// must still reach the engine. The fake emits synchronously inside Start, so the window the
/// bug needed is always open; no delays decide the outcome.
/// </summary>
public sealed class ProcessEarlyOutputTests
{
    private const string Ready = "BYDLO_AMNEZIA_READY";

    private static EngineStartRequest Request() => new()
    {
        Settings = new AppSettings(),
        WorkDirectory = TestDirs.New("early-output"),
    };

    [Fact]
    public async Task Ready_line_printed_before_start_returns_reaches_running()
    {
        var runner = new EarlyRunner(p => p.Emit(Ready));
        await using var engine = new ReadyLineEngine(runner);
        var logs = new List<string>();
        engine.Log += (_, line) => { lock (logs) { logs.Add(line); } };

        await engine.StartAsync(Request());

        Assert.Equal(EngineState.Running, engine.State);
        Assert.True(runner.Process!.SubscribedBeforeDelivery);
        Assert.Equal(1, runner.Process.BeginCalls);
        lock (logs) { Assert.Single(logs, l => l == Ready); }
    }

    [Fact]
    public async Task Fatal_and_exit_before_start_returns_report_the_fatal_line()
    {
        var runner = new EarlyRunner(p =>
        {
            p.Emit("FATAL: invalid AmneziaWG config");
            p.Crash(3);
        });
        var engine = new ReadyLineEngine(runner);
        var states = new List<EngineState>();
        engine.StateChanged += (_, e) => states.Add(e.State);

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));

        Assert.Contains("invalid AmneziaWG config", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.DoesNotContain(EngineState.Running, states);
        Assert.True(runner.Process!.Stopped);
    }

    [Fact]
    public async Task Silent_exit_before_start_returns_reports_the_exit_code()
    {
        var runner = new EarlyRunner(p => p.Crash(5));
        var engine = new ReadyLineEngine(runner);

        var ex = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));

        Assert.Contains("code 5", ex.Message, StringComparison.Ordinal);
        Assert.Equal(EngineState.Failed, engine.State);
    }

    [Fact]
    public async Task Queued_and_later_lines_are_delivered_once_in_order()
    {
        var runner = new EarlyRunner(p =>
        {
            p.Emit("first");
            p.Emit(Ready);
        });
        await using var engine = new ReadyLineEngine(runner);
        var logs = new List<string>();
        engine.Log += (_, line) => { lock (logs) { logs.Add(line); } };

        await engine.StartAsync(Request());
        var process = runner.Process!;
        process.BeginEventDelivery(); // A repeated call must not replay anything.
        process.Emit("after");

        Assert.Equal(2, process.BeginCalls);
        lock (logs) { Assert.Equal(["first", Ready, "after"], logs); }
    }

    /// <summary>Emits from inside Start, before the caller can subscribe.</summary>
    private sealed class EarlyRunner(Action<FakeProcess> early) : IProcessRunner
    {
        public FakeProcess? Process { get; private set; }

        public IManagedProcess Start(ProcessSpec spec)
        {
            Process = new FakeProcess();
            early(Process);
            return Process;
        }

        public Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default) =>
            Task.FromResult((0, "ok"));
    }

    /// <summary>Same readiness contract as AmneziaEngine: only the ready line counts.</summary>
    private sealed class ReadyLineEngine(IProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor())
    {
        // A lost line would surface as a readiness failure; synchronous delivery finishes far sooner.
        protected override TimeSpan ReadyTimeout => TimeSpan.FromSeconds(5);
        protected override bool AliveMeansReady => false;
        protected override bool IsReadyLine(string line) => line == Ready;

        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.AmneziaWG,
            DisplayName = "Fake ready-line engine",
            License = "",
            Upstream = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.AmneziaWG },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };

        public override bool CanHandle(Profile profile) => true;

        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));

        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake", Arguments = [] });
    }
}
