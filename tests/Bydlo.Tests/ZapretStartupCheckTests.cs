using Bydlo.Core.Connection;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>Issue #49: the startup check covers only the remembered strategy.</summary>
public class ZapretStartupCheckTests
{
    private static readonly string[] Targets = ["https://example.invalid/"];

    private static ZapretCheck Ok(string protocol, double ms = 12) =>
        new(Targets[0], protocol, ZapretCheckOutcome.Success, ms);

    private sealed class Probe(Func<IReadOnlyList<ZapretCheck>> check) : IZapretStrategyProbe
    {
        public Task<IReadOnlyList<ZapretCheck>> CheckAsync(IReadOnlyList<string> targets, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(check());
        }
    }

    private static ConnectionManager Manager() => new(new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.001 }),
        new InMemorySecretStore(), new RootedAppPaths(TestDirs.New("startup-check")), new ConflictDetector(new FakePorts()),
        new LogHub(new LogRedactor()), os: OsFamily.Windows);

    [Fact]
    public void Summarize_maps_outcomes_to_verdicts()
    {
        var all = new[] { Ok("HTTP"), Ok("TLS1.2"), Ok("TLS1.3") };
        Assert.Equal(StartupCheckVerdict.Works, ZapretStartupCheck.Summarize(all).Verdict);

        var mixed = new[] { Ok("HTTP"), new ZapretCheck(Targets[0], "TLS1.2", ZapretCheckOutcome.Timeout, null), Ok("TLS1.3") };
        Assert.Equal(StartupCheckVerdict.Partial, ZapretStartupCheck.Summarize(mixed).Verdict);

        var none = new[] { new ZapretCheck(Targets[0], "HTTP", ZapretCheckOutcome.Error, null), new ZapretCheck(Targets[0], "TLS1.2", ZapretCheckOutcome.Timeout, null) };
        Assert.Equal(StartupCheckVerdict.Failed, ZapretStartupCheck.Summarize(none).Verdict);
        Assert.Equal(StartupCheckVerdict.Unavailable, ZapretStartupCheck.Summarize([]).Verdict);
    }

    [Fact]
    public void Summarize_reports_unchecked_when_nothing_conclusive()
    {
        var unknown = new[]
        {
            new ZapretCheck(Targets[0], "HTTP", ZapretCheckOutcome.Unknown, null),
            new ZapretCheck(Targets[0], "TLS1.2", ZapretCheckOutcome.Unknown, null),
            new ZapretCheck(Targets[0], "TLS1.3", ZapretCheckOutcome.Unknown, null),
        };
        Assert.Equal(StartupCheckVerdict.Unavailable, ZapretStartupCheck.Summarize(unknown).Verdict);
        var errored = new[]
        {
            new ZapretCheck(Targets[0], "HTTP", ZapretCheckOutcome.Error, null),
            new ZapretCheck(Targets[0], "TLS1.2", ZapretCheckOutcome.Unknown, null),
        };
        Assert.Equal(StartupCheckVerdict.Failed, ZapretStartupCheck.Summarize(errored).Verdict);
    }

    [Fact]
    public void Summarize_groups_by_target_with_average_latency()
    {
        var summary = ZapretStartupCheck.Summarize([Ok("HTTP", 10), Ok("TLS1.2", 20), Ok("TLS1.3", 30)]);
        Assert.Equal(3, summary.Successes);
        Assert.Equal(3, summary.Total);
        var target = Assert.Single(summary.Targets);
        Assert.Equal(Targets[0], target.Target);
        Assert.Equal(3, target.Successes);
        Assert.Equal(20, target.AverageMilliseconds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_reports_empty_id(string? saved) =>
        Assert.Equal(SavedStrategyState.Empty, ZapretStartupCheck.Classify(saved, ["a.bat"]));

    [Fact]
    public void Classify_reports_missing_id_as_unavailable() =>
        Assert.Equal(SavedStrategyState.Unavailable, ZapretStartupCheck.Classify("gone.bat", ["a.bat", "b.bat"]));

    [Fact]
    public void Classify_accepts_exact_saved_id_only()
    {
        Assert.Equal(SavedStrategyState.Ready, ZapretStartupCheck.Classify("a.bat", ["a.bat", "b.bat"]));
        Assert.Equal(SavedStrategyState.Unavailable, ZapretStartupCheck.Classify("A.BAT", ["a.bat"]));
    }

    [Fact]
    public async Task Check_runs_only_the_saved_strategy_and_restores_state()
    {
        await using var manager = Manager();
        var settings = new AppSettings { MockMode = true, Zapret = new() { Strategy = "saved.bat" } };
        var probe = new Probe(() => [Ok("HTTP"), Ok("TLS1.2"), Ok("TLS1.3")]);
        var report = await manager.CheckZapretStrategyAsync(settings, "saved.bat", Targets, probe);
        Assert.Null(report.Failure);
        Assert.Null(report.RestoreFailure);
        var candidate = Assert.Single(report.Candidates);
        Assert.Equal("saved.bat", candidate.Strategy);
        Assert.All(report.Log, line => Assert.StartsWith("saved.bat", line, StringComparison.Ordinal));
        Assert.Equal(ConnectionStatus.Disconnected, manager.Zapret.Status);
        var summary = ZapretStartupCheck.Summarize(candidate.Rounds.SelectMany(r => r).ToArray());
        Assert.Equal(StartupCheckVerdict.Works, summary.Verdict);
    }
}
