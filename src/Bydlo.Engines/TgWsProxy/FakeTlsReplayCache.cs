namespace Bydlo.Engines.TgWsProxy;

/// <summary>
/// Process-wide replay protection, backported from Flowseal/tg-ws-proxy commit
/// dbc8b7811a1b39f5fd99f3994a8cdfc58e899916 (MIT). Only authenticated hellos enter this cache.
/// </summary>
internal sealed class FakeTlsReplayCache(TimeProvider time, int capacity = 65536)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2 * FakeTls.TimestampTolerance + 1);
    public static readonly FakeTlsReplayCache Shared = new(TimeProvider.System);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _randoms = new(StringComparer.Ordinal);
    private readonly Queue<(string Random, long Accepted)> _accepted = new();

    public bool FirstSeen(ReadOnlySpan<byte> clientRandom)
    {
        var key = Convert.ToHexString(clientRandom);
        lock (_gate)
        {
            var now = time.GetTimestamp();
            while (_accepted.TryPeek(out var oldest) && time.GetElapsedTime(oldest.Accepted, now) >= Window)
            {
                _randoms.Remove(_accepted.Dequeue().Random);
            }

            // Check before eviction: repeating an entry in a full cache must not make it new again.
            if (_randoms.Contains(key))
            {
                return false;
            }

            if (_accepted.Count >= capacity)
            {
                _randoms.Remove(_accepted.Dequeue().Random);
            }

            _randoms.Add(key);
            _accepted.Enqueue((key, now));
            return true;
        }
    }
}
