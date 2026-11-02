using System.Text.Json;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Core.Connection;

public sealed partial class ConnectionManager
{
    /// <summary>Holds the lifecycle gate until tests and restoration finish. No other session can interfere.</summary>
    public async Task<ZapretSelectionReport> SelectZapretStrategyAsync(AppSettings settings, IReadOnlyList<string> strategies,
        IReadOnlyList<string> targets, string? current, IZapretStrategyProbe probe,
        IProgress<ZapretSelectionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new EngineException("A connection operation is already in progress.", localizationKey: "tools.select.busy");
        }
        try
        {
            await probe.PrepareAsync(ct).ConfigureAwait(false);
            var session = CaptureSelectionSession(settings);
            return await new ZapretSelectionService().RunAsync(session, probe, strategies, targets, session.OriginalStrategy ?? current, progress, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Checks only one strategy (the remembered one) with the standard HTTP/TLS probe.
    /// Never enumerates other strategies; the previous session is restored afterwards.
    /// </summary>
    public async Task<ZapretSelectionReport> CheckZapretStrategyAsync(AppSettings settings, string strategy,
        IReadOnlyList<string> targets, IZapretStrategyProbe probe,
        IProgress<ZapretSelectionProgress>? progress = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new EngineException("A connection operation is already in progress.", localizationKey: "tools.select.busy");
        }
        try
        {
            await probe.PrepareAsync(ct).ConfigureAwait(false);
            var session = CaptureSelectionSession(settings);
            return await new ZapretSelectionService().RunAsync(session, probe, [strategy], targets, strategy, progress, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyZapretStrategyAsync(AppSettings settings, string strategy, Func<Task>? save = null, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw new EngineException("A connection operation is already in progress.", localizationKey: "tools.select.busy");
        }
        try
        {
            var session = CaptureSelectionSession(settings);
            try
            {
                await session.StopAsync().ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await session.StartAsync(strategy, deadline.Token).ConfigureAwait(false);
                if (Zapret.Status != ConnectionStatus.Connected)
                {
                    throw new EngineException("Zapret did not remain running.");
                }
                if (save is not null) { await save().ConfigureAwait(false); }
                lock (_sessions) { _sessions[ZapretKey].Settings.AutoReconnect = settings.AutoReconnect; }
            }
            catch (Exception ex)
            {
                try { await session.RestoreAsync().ConfigureAwait(false); }
                catch (Exception restoreError)
                {
                    throw new EngineException(new AggregateException(ex, restoreError).Message, localizationKey: "tools.select.restorefailed");
                }
                throw new EngineException(ex.Message, localizationKey: "tools.select.applyfailed");
            }
        }
        finally { _gate.Release(); }
    }

    private SelectionSession CaptureSelectionSession(AppSettings settings)
    {
        var snapshot = Zapret;
        if (snapshot.CleanupPending || snapshot.Status is not (ConnectionStatus.Connected or ConnectionStatus.Disconnected or ConnectionStatus.Error))
        {
            throw new EngineException("Wait for the current Zapret operation to finish.", localizationKey: "tools.select.busy");
        }
        AppSettings? original = null;
        lock (_sessions)
        {
            if (snapshot.Status == ConnectionStatus.Connected && _sessions.TryGetValue(ZapretKey, out var running))
            {
                original = CloneSettings(running.Settings);
            }
        }
        return new SelectionSession(this, CloneSettings(settings), original);
    }

    private static AppSettings CloneSettings(AppSettings settings) => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

    private sealed class SelectionSession(ConnectionManager manager, AppSettings settings, AppSettings? original) : IZapretSelectionSession
    {
        public bool IsRunning => manager.Zapret.Status == ConnectionStatus.Connected;
        public string? OriginalStrategy => original?.Zapret.Strategy;
        public Task StopAsync() => manager.StopSessionUnsafeAsync(ZapretKey);

        public async Task StartAsync(string strategy, CancellationToken ct)
        {
            var candidate = CloneSettings(settings);
            candidate.Zapret.Strategy = strategy;
            candidate.AutoReconnect = false;
            var blocking = manager._conflicts.CheckZapret(candidate, manager.IsActive(PrimaryKey))
                .FirstOrDefault(i => i.Severity == ConflictSeverity.Blocking);
            if (blocking is not null) { throw new EngineException(blocking.Message); }
            await StartSettingsAsync(candidate, ct).ConfigureAwait(false);
        }

        public async Task RestoreAsync()
        {
            await StopAsync().ConfigureAwait(false);
            if (original is not null)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await StartSettingsAsync(original, deadline.Token).ConfigureAwait(false);
            }
        }

        private async Task StartSettingsAsync(AppSettings value, CancellationToken ct)
        {
            var engine = manager._registry.Create(EngineKind.Zapret, value);
            var secrets = await manager._secrets.LoadAsync(ZapretKey, ct).ConfigureAwait(false);
            await manager.StartSessionUnsafeAsync(new Session(ZapretKey, engine, null, secrets, value), ct).ConfigureAwait(false);
        }
    }
}
