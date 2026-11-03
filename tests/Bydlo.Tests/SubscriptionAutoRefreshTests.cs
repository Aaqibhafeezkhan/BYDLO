using System.Net;
using System.Threading.Channels;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

public sealed class SubscriptionAutoRefreshTests
{
    [Fact]
    public async Task Timer_refreshes_at_interval_without_navigation_and_stops_at_shutdown()
    {
        using var clock = new ManualClock();
        var store = NewStore(clock);
        await store.AddSubscriptionWithProfilesAsync("Test", "https://sub.invalid/list",
            new ProfileImporter().ImportText("trojan://old@old.invalid:443").Profiles);
        using var handler = new SubscriptionHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), clock);
        var settings = new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = 1 };
        var passes = Channel.CreateUnbounded<int>();
        using var stop = new CancellationTokenSource();
        var loop = updater.RunAsync(async ct =>
        {
            var count = await updater.RefreshIfDueAsync(settings, false, ex => Assert.Fail(ex.ToString()), ct);
            await passes.Writer.WriteAsync(count, ct);
        }, stop.Token);

        Assert.Equal(0, await NextPass());
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(0, await NextPass());
        Assert.Equal(0, handler.Requests);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await NextPass());
        Assert.Equal("new.invalid", Assert.Single(store.Profiles).Host);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Subscriptions).LastUpdated);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await NextPass());
        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, await NextPass());
        Assert.Equal(2, handler.Requests);

        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(2, handler.Requests);
        Assert.True(clock.TimerDisposed);

        async Task<int> NextPass() => await passes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Current_settings_and_safety_gate_are_respected_on_each_pass()
    {
        using var clock = new ManualClock();
        var store = NewStore(clock);
        await store.AddSubscriptionAsync("Never refreshed", "https://sub.invalid/list");
        using var handler = new SubscriptionHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), clock);
        var settings = new AppSettings { SubscriptionAutoUpdate = false, SubscriptionUpdateIntervalHours = 12 };
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, Fail));
        settings.SubscriptionAutoUpdate = true;
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, true, Fail));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(1, await updater.RefreshIfDueAsync(settings, false, Fail));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, Fail));
        settings.SubscriptionUpdateIntervalHours = 1;
        Assert.Equal(1, await updater.RefreshIfDueAsync(settings, false, Fail));
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_subscription_does_not_block_others_and_is_retried(bool throws)
    {
        using var clock = new ManualClock();
        var store = NewStore(clock);
        var bad = await store.AddSubscriptionAsync("Bad", "https://bad.invalid/list");
        await store.AddSubscriptionAsync("Good", "https://sub.invalid/list");
        using var handler = new SubscriptionHandler { FailBadHost = true, ThrowOnFailure = throws };
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), clock);
        var errors = new List<Exception>();
        Assert.Equal(1, await updater.RefreshIfDueAsync(new AppSettings(), false, errors.Add));
        Assert.Null(store.Subscriptions.Single(s => s.Id == bad.Id).LastUpdated);
        Assert.Equal(throws ? 1 : 0, errors.Count);
        handler.FailBadHost = false;
        Assert.Equal(1, await updater.RefreshIfDueAsync(new AppSettings(), false, Fail));
        Assert.Equal(3, handler.Requests);
        Assert.All(store.Subscriptions, s => Assert.NotNull(s.LastUpdated));
    }

    [Fact]
    public async Task Timer_waits_for_inflight_work_and_cancels_it_on_shutdown()
    {
        using var clock = new ManualClock();
        using var handler = new SubscriptionHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(NewStore(clock), new SubscriptionClient(http, new ProfileImporter()), clock);
        using var stop = new CancellationTokenSource();
        var calls = 0;
        var loop = updater.RunAsync(async ct =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, stop.Token);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, calls);
        stop.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
        Assert.True(clock.TimerDisposed);
    }

    private static void Fail(Exception ex) => Assert.Fail(ex.ToString());

    private static ProfileStore NewStore(TimeProvider clock) => new(
        Path.Combine(TestDirs.New("sub-schedule"), "profiles.json"), new InMemorySecretStore(), clock);

    private sealed class SubscriptionHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool FailBadHost { get; set; }
        public bool ThrowOnFailure { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (FailBadHost && request.RequestUri!.Host == "bad.invalid")
            {
                if (ThrowOnFailure) { throw new HttpRequestException("Fixture failure"); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("trojan://new@new.invalid:443"),
            });
        }
    }

    /// <summary>Drives the real PeriodicTimer without wall-clock delays or external services.</summary>
    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private ManualTimer? _timer;
        public bool TimerDisposed => _timer?.Disposed == true;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Dispose() => _timer?.Dispose();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timer = new ManualTimer(callback, state, _now + dueTime, period);
            return _timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            _timer?.Tick(_now);
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset next, TimeSpan period) : ITimer
        {
            public bool Disposed { get; private set; }
            public void Tick(DateTimeOffset now)
            {
                if (Disposed || now < next) { return; }
                while (next <= now) { next += period; }
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
