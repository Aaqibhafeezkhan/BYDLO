using Bydlo.Core.Models;

namespace Bydlo.Engines.Groups;

internal interface IGroupController
{
    Task<IReadOnlyList<GroupProbe>> ProbeAsync(ServerGroup group, IReadOnlyList<ChainHop> members, CancellationToken ct);
    Task SelectAsync(Guid? id, CancellationToken ct);
    Task<Guid?> CurrentAsync(CancellationToken ct);
}

/// <summary>Owns one loop per engine lifetime. Stop joins it before the controller is disposed.</summary>
internal sealed class GroupMonitor(ServerGroup group, IReadOnlyList<ChainHop> members, IGroupController controller,
    bool nativeUrlTest) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private readonly object _sync = new();
    private Guid? _selected;
    private Guid? _lastTarget;
    public Guid? Selected { get { lock (_sync) { return _selected; } } }
    public void Start() => _loop = RunAsync();

    internal async Task TickAsync(CancellationToken ct)
    {
        Guid? selected;
        if (group.Mode == ServerGroupMode.Manual)
        {
            selected = group.InitialProfileId(members);
            await controller.SelectAsync(selected, ct).ConfigureAwait(false);
            selected = await controller.CurrentAsync(ct).ConfigureAwait(false);
        }
        else if (nativeUrlTest && group.Mode == ServerGroupMode.UrlTest)
        {
            selected = await controller.CurrentAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var probes = await controller.ProbeAsync(group, members, ct).ConfigureAwait(false);
            selected = GroupSelection.Pick(group, _lastTarget ?? group.InitialProfileId(members), probes);
            await controller.SelectAsync(selected, ct).ConfigureAwait(false);
            // Confirm the target at the core before publishing it to Home.
            selected = selected is null ? null : await controller.CurrentAsync(ct).ConfigureAwait(false);
        }
        _lastTarget = selected;
        lock (_sync) { _selected = selected; }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await TickAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
                    or Grpc.Core.RpcException or InvalidOperationException or TaskCanceledException)
                {
                    lock (_sync) { _selected = null; }
                }
                var seconds = nativeUrlTest ? group.Mode == ServerGroupMode.UrlTest ? 1 : group.IntervalSeconds : Math.Min(group.IntervalSeconds, 5);
                await Task.Delay(TimeSpan.FromSeconds(seconds), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null) { await _loop.ConfigureAwait(false); }
        _stop.Dispose();
    }
}
