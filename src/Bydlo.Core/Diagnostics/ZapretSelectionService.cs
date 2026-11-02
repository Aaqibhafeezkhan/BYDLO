namespace Bydlo.Core.Diagnostics;

public enum ZapretCheckOutcome { Success, Error, Timeout, Unknown }

public sealed record ZapretCheck(string Target, string Protocol, ZapretCheckOutcome Outcome, double? Milliseconds, string? Detail = null);

public sealed record ZapretCandidateResult(string Strategy, List<List<ZapretCheck>> Rounds)
{
    public int Successes => Rounds.Sum(r => r.Count(c => c.Outcome == ZapretCheckOutcome.Success));
    public int Errors => Rounds.Sum(r => r.Count(c => c.Outcome == ZapretCheckOutcome.Error));
    public int Timeouts => Rounds.Sum(r => r.Count(c => c.Outcome == ZapretCheckOutcome.Timeout));
    public int Unknown => Rounds.Sum(r => r.Count(c => c.Outcome == ZapretCheckOutcome.Unknown));
    public int StableSuccesses => Rounds.Count == 2 ? Rounds.Min(r => r.Count(c => c.Outcome == ZapretCheckOutcome.Success)) : 0;
    public double? Milliseconds => Rounds.SelectMany(r => r).Where(c => c.Outcome == ZapretCheckOutcome.Success && c.Milliseconds.HasValue)
        .Select(c => c.Milliseconds).DefaultIfEmpty(null).Average();
}

public sealed record ZapretSelectionProgress(string Stage, string? Strategy, int Completed, int Total, int Successes = 0, int Errors = 0, int Timeouts = 0, int Unknown = 0);

public sealed class ZapretSelectionReport
{
    public DateTimeOffset TestedAt { get; set; } = DateTimeOffset.Now;
    public string? OriginalStrategy { get; set; }
    public string? RecommendedStrategy { get; set; }
    public string? AppliedStrategy { get; set; }
    public List<string> Targets { get; set; } = [];
    public List<ZapretCandidateResult> Candidates { get; set; } = [];
    public List<string> Log { get; set; } = [];
    public string? Failure { get; set; }
    public string? RestoreFailure { get; set; }
    public bool Cancelled { get; set; }
}

/// <summary>Owns only the application's Zapret session. Restore must ignore the cancelled test token.</summary>
public interface IZapretSelectionSession
{
    bool IsRunning { get; }
    Task StartAsync(string strategy, CancellationToken ct);
    Task StopAsync();
    Task RestoreAsync();
}

public interface IZapretStrategyProbe
{
    Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;
    Task<IReadOnlyList<ZapretCheck>> CheckAsync(IReadOnlyList<string> targets, CancellationToken ct);
}

/// <summary>Pure orchestration; production session and probes are supplied by ConnectionManager.</summary>
public sealed class ZapretSelectionService
{
    private static readonly string[] Protocols = ["HTTP", "TLS1.2", "TLS1.3"];
    public async Task<ZapretSelectionReport> RunAsync(IZapretSelectionSession session, IZapretStrategyProbe probe,
        IReadOnlyList<string> strategies, IReadOnlyList<string> targets, string? current,
        IProgress<ZapretSelectionProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new ZapretSelectionReport { OriginalStrategy = current, Targets = [.. targets] };
        try
        {
            var completed = 0;
            foreach (var strategy in strategies.Distinct(StringComparer.Ordinal))
            {
                var row = new ZapretCandidateResult(strategy, []);
                report.Candidates.Add(row);
                for (var round = 0; round < 2; round++)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new("starting", strategy, completed, strategies.Count * 2));
                    try
                    {
                        // Never carry a previous candidate into the next check.
                        await session.StopAsync().ConfigureAwait(false);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        deadline.CancelAfter(TimeSpan.FromSeconds(15));
                        await session.StartAsync(strategy, deadline.Token).ConfigureAwait(false);
                        progress?.Report(new("checking", strategy, completed, strategies.Count * 2));
                        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        probeDeadline.CancelAfter(TimeSpan.FromSeconds(100));
                        var checks = await probe.CheckAsync(targets, probeDeadline.Token).ConfigureAwait(false);
                        if (checks.Count != targets.Count * Protocols.Length || targets.Any(t => Protocols.Any(p => checks.Count(c => c.Target == t && c.Protocol == p) != 1)))
                        {
                            throw new InvalidDataException("Incomplete Test Zapret standard results.");
                        }
                        if (!session.IsRunning) { throw new InvalidOperationException("Zapret exited during the checks."); }
                        row.Rounds.Add([.. checks]);
                        foreach (var check in checks)
                        {
                            report.Log.Add($"{strategy} [{round + 1}] {check.Target} {check.Protocol}: {check.Outcome} {check.Milliseconds} {check.Detail}");
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        row.Rounds.Add([.. targets.SelectMany(t => Protocols
                            .Select(p => new ZapretCheck(t, p, ex is OperationCanceledException or TimeoutException ? ZapretCheckOutcome.Timeout : ZapretCheckOutcome.Unknown, null, ex.Message)))]);
                        report.Log.Add($"{strategy}: {ex.Message}");
                    }
                    finally
                    {
                        // Cleanup failures abort the whole search; testing another strategy would contaminate its result.
                        await session.StopAsync().ConfigureAwait(false);
                    }
                    completed++;
                    progress?.Report(new("checked", strategy, completed, strategies.Count * 2, row.Successes, row.Errors, row.Timeouts, row.Unknown));
                }
            }
            ct.ThrowIfCancellationRequested();
            report.RecommendedStrategy = Rank(report.Candidates, current).FirstOrDefault(r => r.StableSuccesses > 0)?.Strategy;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { report.Cancelled = true; }
        catch (Exception ex) { report.Failure = ex.Message; }
        finally
        {
            progress?.Report(new("restoring", current, 0, 0));
            try { await session.RestoreAsync().ConfigureAwait(false); }
            catch (Exception ex)
            {
                report.RestoreFailure = ex.Message;
                report.RecommendedStrategy = null;
            }
        }
        return report;
    }

    public static IOrderedEnumerable<ZapretCandidateResult> Rank(IEnumerable<ZapretCandidateResult> rows, string? current) => rows
        .OrderByDescending(r => r.StableSuccesses).ThenByDescending(r => r.Successes)
        .ThenBy(r => r.Milliseconds ?? double.MaxValue)
        .ThenByDescending(r => string.Equals(r.Strategy, current, StringComparison.Ordinal))
        .ThenBy(r => r.Strategy, StringComparer.Ordinal);
}
