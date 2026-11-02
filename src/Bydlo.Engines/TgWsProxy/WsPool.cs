namespace Bydlo.Engines.TgWsProxy;

/// <summary>
/// Keeps a few ready WebSocket connections per DC (and media / test flavour) so a new Telegram connection
/// does not pay for TCP + TLS + upgrade. Idle sockets are rotated after <see cref="MaxAge"/>.
/// </summary>
internal sealed class WsPool(TgWsProxyContext context)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffInitial = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromHours(1);

    /// <summary>SNI used for domain fronting when the direct name is filtered.</summary>
    public const string FrontingSni = "sprinthost.ru";

    private readonly Lock _gate = new();
    private readonly Dictionary<PoolKey, Bucket> _buckets = [];
    private volatile bool _frontingFirst;

    public static IReadOnlyList<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203)
        {
            dc = 2;
        }

        return isMedia ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"] : [$"kws{dc}.web.telegram.org"];
    }

    /// <summary>A pooled connection, or null (the caller falls back) — a refill is scheduled either way.</summary>
    public RawWebSocket? Get(int dc, bool isMedia, bool isTestDc)
    {
        var options = context.Options;
        if (options.PoolSize <= 0 || !options.DcRedirects.TryGetValue(dc, out var ip))
        {
            return null;
        }

        var key = new PoolKey(dc, isMedia, isTestDc, ip);
        var now = context.Time.GetUtcNow();
        RawWebSocket? hit = null;
        lock (_gate)
        {
            var bucket = BucketFor(key);
            while (bucket.Idle.Count > 0)
            {
                var (ws, created) = bucket.Idle.Dequeue();
                if (IsStale(ws, created, now))
                {
                    _ = ws.DisposeAsync().AsTask();
                    continue;
                }

                hit = ws;
                break;
            }
        }

        if (hit is not null)
        {
            context.Stats.PoolHit();
        }
        else
        {
            context.Stats.PoolMiss();
        }

        ScheduleRefill(key);
        return hit;
    }

    /// <summary>Starts filling the pool for every configured DC (regular and media).</summary>
    public void Warmup()
    {
        foreach (var (dc, ip) in context.Options.DcRedirects)
        {
            foreach (var media in new[] { false, true })
            {
                ScheduleRefill(new PoolKey(dc, media, context.Options.ForceTestDc, ip));
            }
        }

        context.Log(TgLogLevel.Info, $"WS pool warmup started for {context.Options.DcRedirects.Count} DC(s)");
    }

    /// <summary>Drops stale sockets and refills; runs until the proxy stops.</summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(CheckInterval, context.Time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var now = context.Time.GetUtcNow();
            List<PoolKey> keys;
            var expired = new List<RawWebSocket>();
            lock (_gate)
            {
                foreach (var bucket in _buckets.Values)
                {
                    var ready = new Queue<(RawWebSocket, DateTimeOffset)>();
                    while (bucket.Idle.Count > 0)
                    {
                        var item = bucket.Idle.Dequeue();
                        if (IsStale(item.Ws, item.Created, now)) { expired.Add(item.Ws); }
                        else { ready.Enqueue(item); }
                    }

                    bucket.Idle = ready;
                }

                keys = [.. _buckets.Where(b => b.Value.Idle.Count < context.Options.PoolSize).Select(b => b.Key)];
            }

            foreach (var ws in expired)
            {
                await ws.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var key in keys)
            {
                ScheduleRefill(key);
            }
        }
    }

    public async Task CloseAsync()
    {
        List<RawWebSocket> idle;
        List<Task> refills;
        lock (_gate)
        {
            idle = [.. _buckets.Values.SelectMany(b => b.Idle.Select(i => i.Ws))];
            refills = [.. _buckets.Values.Select(b => b.Refill).OfType<Task>()];
            _buckets.Clear();
        }

        foreach (var ws in idle)
        {
            await ws.DisposeAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(refills).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static bool IsStale(RawWebSocket ws, DateTimeOffset created, DateTimeOffset now) => ws.IsClosed || now - created >= MaxAge;

    private Bucket BucketFor(PoolKey key)
    {
        if (!_buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket();
            _buckets[key] = bucket;
        }

        return bucket;
    }

    private void ScheduleRefill(PoolKey key)
    {
        if (context.Options.PoolSize <= 0 || context.Stopping.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            var bucket = BucketFor(key);
            if (bucket.Refill is { IsCompleted: false } || context.Time.GetUtcNow() < bucket.RetryAfter)
            {
                return;
            }

            bucket.Refill = Task.Run(() => RefillAsync(key, bucket));
        }
    }

    private async Task RefillAsync(PoolKey key, Bucket bucket)
    {
        int needed;
        lock (_gate)
        {
            needed = context.Options.PoolSize - bucket.Idle.Count;
        }

        if (needed <= 0)
        {
            return;
        }

        var path = key.IsTestDc ? "/apiws_test" : "/apiws";
        var domains = WsDomains(key.Dc, key.IsMedia);
        var results = await Task.WhenAll(Enumerable.Range(0, needed).Select(_ => ConnectOneAsync(key.Ip, domains, path))).ConfigureAwait(false);
        var connected = results.OfType<RawWebSocket>().ToList();

        var adopted = false;
        lock (_gate)
        {
            if (!context.Stopping.IsCancellationRequested && _buckets.TryGetValue(key, out var current) && current == bucket)
            {
                foreach (var ws in connected)
                {
                    bucket.Idle.Enqueue((ws, context.Time.GetUtcNow()));
                }

                adopted = true;
                if (connected.Count > 0)
                {
                    bucket.Failures = 0;
                    bucket.RetryAfter = DateTimeOffset.MinValue;
                }
                else
                {
                    bucket.Failures++;
                    var delay = BackoffInitial * Math.Pow(2, Math.Min(bucket.Failures - 1, 12));
                    bucket.RetryAfter = context.Time.GetUtcNow() + (delay < BackoffMax ? delay : BackoffMax);
                }
            }
        }

        if (!adopted)
        {
            foreach (var ws in connected)
            {
                await ws.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        if (connected.Count == 0)
        {
            context.Log(TgLogLevel.Info, $"WS pool refill failed for {key}, retry in {(bucket.RetryAfter - context.Time.GetUtcNow()).TotalSeconds:0}s");
        }
        else
        {
            context.Log(TgLogLevel.Debug, $"WS pool refilled {key}: {connected.Count} new");
        }
    }

    private async Task<RawWebSocket?> ConnectOneAsync(string ip, IReadOnlyList<string> domains, string path)
    {
        foreach (var domain in domains)
        {
            bool[] modes = _frontingFirst ? [true, false] : [false, true];
            foreach (var fronted in modes)
            {
                try
                {
                    var ws = await RawWebSocket.ConnectAsync(context.Network, ip, domain, path, TimeSpan.FromSeconds(fronted ? 7 : 8),
                        context.Stopping, sni: fronted ? FrontingSni : null).ConfigureAwait(false);
                    _frontingFirst = fronted;
                    if (fronted)
                    {
                        context.Stats.Fronting();
                    }

                    return ws;
                }
                catch (Exception ex) when (!context.Stopping.IsCancellationRequested)
                {
                    context.Stats.WsError();
                    context.Log(TgLogLevel.Debug, $"WS pool connect {domain}{path} via {ip} (fronting={fronted}): {ex.GetType().Name}: {ex.Message}");
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private sealed record PoolKey(int Dc, bool IsMedia, bool IsTestDc, string Ip)
    {
        public override string ToString() => $"DC{Dc}{(IsTestDc ? "t" : "")}{(IsMedia ? "m" : "")}";
    }

    private sealed class Bucket
    {
        public Queue<(RawWebSocket Ws, DateTimeOffset Created)> Idle { get; set; } = new();

        public Task? Refill { get; set; }

        public int Failures { get; set; }

        public DateTimeOffset RetryAfter { get; set; } = DateTimeOffset.MinValue;
    }
}
