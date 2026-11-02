using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.Zapret;

namespace Bydlo.Tests;

public class ZapretSelectionTests
{
    private static readonly string[] Strategies = ["general.bat", "general (ALT Кириллица).bat"];
    private static readonly string[] Targets = ["https://example.invalid/"];

    private sealed class Session : IZapretSelectionSession
    {
        public List<string> Calls { get; } = [];
        public string? Current { get; private set; }
        public bool IsRunning { get; set; }
        public Exception? StartError { get; set; }
        public bool StopFails { get; set; }
        public bool RestoreFails { get; set; }
        public Task StartAsync(string strategy, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add("start:" + strategy);
            if (StartError is not null) { throw StartError; }
            Assert.Null(Current);
            Current = strategy;
            IsRunning = true;
            return Task.CompletedTask;
        }
        public Task StopAsync()
        {
            Calls.Add("stop");
            if (StopFails && Current is not null) { throw new IOException("cleanup failed"); }
            Current = null;
            IsRunning = false;
            return Task.CompletedTask;
        }
        public async Task RestoreAsync()
        {
            Calls.Add("restore");
            if (RestoreFails) { throw new IOException("restore failed"); }
            await StopAsync();
            Current = "original";
        }
    }

    private sealed class Probe(Func<IReadOnlyList<ZapretCheck>> check) : IZapretStrategyProbe
    {
        public Task<IReadOnlyList<ZapretCheck>> CheckAsync(IReadOnlyList<string> targets, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(check());
        }
    }

    private static List<ZapretCheck> Checks(int successes = 3, double latency = 50) => [.. TestZapretProbe.Protocols.Select((p, i) =>
        new ZapretCheck(Targets[0], p, i < successes ? ZapretCheckOutcome.Success : ZapretCheckOutcome.Error, i < successes ? latency : null))];

    [Fact]
    public async Task Tests_each_exact_configuration_twice_and_restores_before_returning()
    {
        var session = new Session();
        var report = await new ZapretSelectionService().RunAsync(session,
            new Probe(() => Checks(session.Current == Strategies[1] ? 3 : 2)), Strategies, Targets, Strategies[0]);
        Assert.Equal(Strategies[1], report.RecommendedStrategy);
        Assert.All(report.Candidates, r => Assert.Equal(2, r.Rounds.Count));
        Assert.Equal("original", session.Current);
        Assert.Equal(4, session.Calls.Count(c => c.StartsWith("start:", StringComparison.Ordinal)));
        Assert.Null(report.Failure);
    }

    [Fact]
    public void Stable_success_then_latency_and_current_configuration_break_ties()
    {
        var stable = new ZapretCandidateResult("stable", [Checks(2), Checks(2)]);
        var unstable = new ZapretCandidateResult("unstable", [Checks(3), Checks(1)]);
        Assert.Equal("stable", ZapretSelectionService.Rank([unstable, stable], null).First().Strategy);
        var current = new ZapretCandidateResult("current", [Checks(2), Checks(2)]);
        Assert.Equal("current", ZapretSelectionService.Rank([stable, current], "current").First().Strategy);
        var faster = new ZapretCandidateResult("faster", [Checks(2, 10), Checks(2, 10)]);
        Assert.Equal("faster", ZapretSelectionService.Rank([stable, faster], "stable").First().Strategy);
    }

    [Fact]
    public async Task All_failed_or_malformed_checks_never_recommend_a_configuration()
    {
        foreach (var checks in new[] { Checks(0), Checks().Take(2).ToList(), new List<ZapretCheck> { Checks()[0], Checks()[0], Checks()[0] } })
        {
            var session = new Session();
            var report = await new ZapretSelectionService().RunAsync(session, new Probe(() => checks), Strategies, Targets, Strategies[0]);
            Assert.Null(report.RecommendedStrategy);
            Assert.Equal("original", session.Current);
        }
    }

    [Fact]
    public async Task Cancellation_stops_testing_and_restores_without_the_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        var session = new Session();
        var report = await new ZapretSelectionService().RunAsync(session, new Probe(() =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }), Strategies, Targets, null, ct: cancellation.Token);
        Assert.True(report.Cancelled);
        Assert.Null(report.RecommendedStrategy);
        Assert.Equal("original", session.Current);
        Assert.Single(session.Calls, c => c.StartsWith("start:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_and_probe_timeouts_restore_the_original_state(bool startup)
    {
        var session = new Session { StartError = startup ? new TimeoutException("hung startup") : null };
        var report = await new ZapretSelectionService().RunAsync(session, new Probe(() => throw new TimeoutException("hung probe")), Strategies, Targets, null);
        Assert.All(report.Candidates, r => Assert.Equal(6, r.Timeouts));
        Assert.Null(report.RecommendedStrategy);
        Assert.Equal("original", session.Current);
    }

    [Fact]
    public async Task Cleanup_failure_aborts_and_failed_restoration_removes_recommendation()
    {
        var session = new Session { StopFails = true, RestoreFails = true };
        var report = await new ZapretSelectionService().RunAsync(session, new Probe(() => Checks()), Strategies, Targets, null);
        Assert.NotNull(report.Failure);
        Assert.NotNull(report.RestoreFailure);
        Assert.Null(report.RecommendedStrategy);
        Assert.Single(report.Candidates);
    }

    [Fact]
    public async Task Crash_during_probes_cannot_be_scored_as_success()
    {
        var session = new Session();
        var report = await new ZapretSelectionService().RunAsync(session, new Probe(() =>
        {
            session.IsRunning = false;
            return Checks();
        }), Strategies, Targets, null);
        Assert.Null(report.RecommendedStrategy);
        Assert.All(report.Candidates, r => Assert.Equal(0, r.Successes));
    }

    [Theory]
    [InlineData(0, "BYDLO_RESULT:200:0.125", ZapretCheckOutcome.Success, 125)]
    [InlineData(0, "BYDLO_RESULT:403:0.001", ZapretCheckOutcome.Error, -1)]
    [InlineData(28, "BYDLO_RESULT:000:4.0", ZapretCheckOutcome.Timeout, -1)]
    [InlineData(35, "TLS unsupported", ZapretCheckOutcome.Unknown, -1)]
    [InlineData(0, "new unknown format", ZapretCheckOutcome.Unknown, -1)]
    [InlineData(60, "certificate error", ZapretCheckOutcome.Error, -1)]
    public void Parses_actual_curl_results_without_counting_fast_errors_as_success(int code, string output, ZapretCheckOutcome expected, int ms)
    {
        var result = TestZapretProbe.Parse(Targets[0], "HTTP", code, output);
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(ms < 0 ? null : (double?)ms, result.Milliseconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://user:password@example.invalid/")]
    [InlineData("https://example.invalid/?token=secret")]
    public void Rejects_invalid_or_secret_bearing_targets(string text) => Assert.Throws<FormatException>(() => TestZapretProbe.ParseTargets(text));

    [Fact]
    public async Task Standard_probe_uses_fake_runner_and_passes_urls_as_arguments()
    {
        var fake = new FakeProcessRunner { OnCompletion = _ => (0, "BYDLO_RESULT:204:0.250") };
        var result = await new TestZapretProbe(fake).CheckWithRunnerAsync(Targets, CancellationToken.None);
        Assert.Equal(3, result.Count);
        Assert.All(result, c => Assert.Equal(ZapretCheckOutcome.Success, c.Outcome));
        Assert.All(fake.Completed, spec =>
        {
            Assert.True(spec.AffectsNetwork);
            Assert.Equal(Targets[0], spec.Arguments[^1]);
            Assert.Contains("--noproxy", spec.Arguments);
            Assert.Contains("--disable", spec.Arguments);
        });
    }

    [Fact]
    public async Task Probe_preflight_checks_curl_without_network_and_fails_before_testing()
    {
        var runner = new FakeProcessRunner { OnCompletion = _ => (0, "curl 8.10.1 libcurl/8.10.1") };
        await new TestZapretProbe(runner).PrepareAsync(CancellationToken.None);
        Assert.Single(runner.Completed);
        Assert.False(runner.Completed[0].AffectsNetwork);
        runner.OnCompletion = _ => (1, "missing");
        var error = await Assert.ThrowsAsync<EngineException>(() => new TestZapretProbe(runner).PrepareAsync(CancellationToken.None));
        Assert.Equal("tools.select.curl", error.LocalizationKey);
    }
    private static ConnectionManager Manager() => new(new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.001 }),
        new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("selection")), new ConflictDetector(new FakePorts()),
        new LogHub(new LogRedactor()), os: OsFamily.Windows);

    [Fact]
    public async Task Manager_restores_running_session_and_rejects_parallel_searches()
    {
        await using var manager = Manager();
        var settings = new AppSettings { MockMode = true, Zapret = new() { Strategy = "original.bat" } };
        await manager.StartZapretAsync(settings);
        var probe = new BlockingProbe();
        var first = manager.SelectZapretStrategyAsync(settings, Strategies, Targets, "original.bat", probe);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<EngineException>(() => manager.SelectZapretStrategyAsync(settings, Strategies, Targets, null, probe));
        probe.Release.SetResult();
        var report = await first;
        Assert.Null(report.RestoreFailure);
        Assert.Equal(ConnectionStatus.Connected, manager.Zapret.Status);
        Assert.Equal("original.bat", settings.Zapret.Strategy);
    }

    [Fact]
    public async Task Failed_apply_rolls_back_running_or_stopped_state()
    {
        foreach (var running in new[] { true, false })
        {
            await using var manager = Manager();
            var settings = new AppSettings { MockMode = true };
            if (running) { await manager.StartZapretAsync(settings); }
            var failing = new AppSettings { MockMode = true, MockScenario = MockScenario.FailOnConnect };
            var error = await Assert.ThrowsAsync<EngineException>(() => manager.ApplyZapretStrategyAsync(failing, Strategies[0]));
            Assert.Equal("tools.select.applyfailed", error.LocalizationKey);
            Assert.Equal(running ? ConnectionStatus.Connected : ConnectionStatus.Disconnected, manager.Zapret.Status);
        }
    }

    [Fact]
    public async Task Successful_apply_validates_start_and_save_failure_rolls_back()
    {
        await using var manager = Manager();
        var settings = new AppSettings { MockMode = true };
        await manager.ApplyZapretStrategyAsync(settings, Strategies[0]);
        Assert.Equal(ConnectionStatus.Connected, manager.Zapret.Status);
        await Assert.ThrowsAsync<EngineException>(() => manager.ApplyZapretStrategyAsync(settings, Strategies[1], save: () => throw new IOException("disk full")));
        Assert.Equal(ConnectionStatus.Connected, manager.Zapret.Status);
    }

    private sealed class BlockingProbe : IZapretStrategyProbe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ZapretCheck>> CheckAsync(IReadOnlyList<string> targets, CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return Checks();
        }
    }
}

