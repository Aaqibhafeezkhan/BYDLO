namespace Bydlo.Core.Diagnostics;

/// <summary>
/// Verdict of the saved-strategy startup check. Describes only HTTP/TLS probe
/// success; it never claims that video, voice or UDP work.
/// </summary>
public enum StartupCheckVerdict { Works, Partial, Failed }

/// <summary>How the remembered strategy id relates to the installed strategies.</summary>
public enum SavedStrategyState { Ready, Empty, Unavailable }

public sealed record StartupCheckTargetResult(string Target, int Successes, int Total, double? AverageMilliseconds);

public sealed record StartupCheckSummary(StartupCheckVerdict Verdict, int Successes, int Total, IReadOnlyList<StartupCheckTargetResult> Targets);

/// <summary>Pure summary of a single-strategy probe run. No processes, no network.</summary>
public static class ZapretStartupCheck
{
    /// <summary>Empty when nothing was ever saved; Unavailable when the saved id is gone.</summary>
    public static SavedStrategyState Classify(string? saved, IEnumerable<string> available) =>
        string.IsNullOrWhiteSpace(saved) ? SavedStrategyState.Empty
        : available.Contains(saved, StringComparer.Ordinal) ? SavedStrategyState.Ready
        : SavedStrategyState.Unavailable;

    public static StartupCheckSummary Summarize(IReadOnlyList<ZapretCheck> checks)
    {
        var list = checks.ToArray();
        var targets = list.GroupBy(c => c.Target, StringComparer.Ordinal)
            .Select(g =>
            {
                var ok = g.Where(c => c.Outcome == ZapretCheckOutcome.Success && c.Milliseconds.HasValue)
                    .Select(c => c.Milliseconds!.Value).ToArray();
                return new StartupCheckTargetResult(g.Key, g.Count(c => c.Outcome == ZapretCheckOutcome.Success),
                    g.Count(), ok.Length == 0 ? null : (double?)ok.Average());
            }).ToArray();
        var successes = list.Count(c => c.Outcome == ZapretCheckOutcome.Success);
        var verdict = list.Length == 0 || successes == 0 ? StartupCheckVerdict.Failed
            : successes == list.Length ? StartupCheckVerdict.Works
            : StartupCheckVerdict.Partial;
        return new StartupCheckSummary(verdict, successes, list.Length, targets);
    }
}
