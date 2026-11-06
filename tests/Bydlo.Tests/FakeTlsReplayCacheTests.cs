using Bydlo.Engines.TgWsProxy;

namespace Bydlo.Tests;

public sealed class FakeTlsReplayCacheTests
{
    [Fact]
    public void Repeat_is_rejected_until_the_full_timestamp_window_expires()
    {
        var clock = new ReplayClock();
        var cache = new FakeTlsReplayCache(clock);
        Assert.True(cache.FirstSeen([1]));
        clock.Advance(TimeSpan.FromSeconds(2 * FakeTls.TimestampTolerance + 0.999));
        Assert.False(cache.FirstSeen([1]));
        clock.Advance(TimeSpan.FromSeconds(0.001));
        Assert.True(cache.FirstSeen([1]));
        Assert.False(cache.FirstSeen([1]));
    }

    [Fact]
    public void Full_cache_checks_repeats_before_evicting_the_oldest()
    {
        var cache = new FakeTlsReplayCache(new ReplayClock(), capacity: 2);
        Assert.True(cache.FirstSeen([1]));
        Assert.True(cache.FirstSeen([2]));
        Assert.False(cache.FirstSeen([1]));
        Assert.False(cache.FirstSeen([2]));
        Assert.True(cache.FirstSeen([3]));
        Assert.False(cache.FirstSeen([2]));
        Assert.False(cache.FirstSeen([3]));
        Assert.True(cache.FirstSeen([1]));
    }

    [Fact]
    public void Expiration_uses_monotonic_time_even_if_wall_clock_changes()
    {
        var clock = new ReplayClock();
        var cache = new FakeTlsReplayCache(clock);
        Assert.True(cache.FirstSeen([1]));
        clock.UtcNow = clock.UtcNow.AddYears(1);
        Assert.False(cache.FirstSeen([1]));
        clock.UtcNow = clock.UtcNow.AddYears(-2);
        Assert.False(cache.FirstSeen([1]));
        clock.Advance(FakeTlsReplayCache.Window);
        Assert.True(cache.FirstSeen([1]));
    }

    [Fact]
    public void Concurrent_repeats_are_accepted_only_once()
    {
        var cache = new FakeTlsReplayCache(new ReplayClock());
        var accepted = 0;
        Parallel.For(0, 128, _ =>
        {
            if (cache.FirstSeen([1, 2, 3])) { Interlocked.Increment(ref accepted); }
        });
        Assert.Equal(1, accepted);
    }
}

internal sealed class ReplayClock : TimeProvider
{
    private long _ticks;
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    public override DateTimeOffset GetUtcNow() => UtcNow;
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan elapsed)
    {
        _ticks += elapsed.Ticks;
        UtcNow += elapsed;
    }
}
