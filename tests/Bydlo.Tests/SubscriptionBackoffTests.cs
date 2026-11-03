using System.Net;
using Bydlo.Core.Import;
using Bydlo.Core.Models;
using Bydlo.Core.Security;
using Bydlo.Core.Storage;

namespace Bydlo.Tests;

/// <summary>Covers issue #206: failed auto-refresh backs off instead of retrying every minute.</summary>
public sealed class SubscriptionBackoffTests
{
    [Fact]
    public async Task Failed_refresh_is_not_retried_before_backoff()
    {
        using var clock = new ManualClock();
        var store = NewStore(clock);
        await store.AddSubscriptionAsync("Bad", "https://bad.invalid/list");
        using var handler = new FailingHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), clock);
        var settings = new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = 12 };
        var errors = new List<Exception>();

        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, errors.Add));
        Assert.Equal(1, handler.Requests);
        Assert.Single(errors);

        errors.Clear();
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, errors.Add));
        Assert.Equal(1, handler.Requests);
        Assert.Empty(errors);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, errors.Add));
        Assert.Equal(1, handler.Requests);

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, errors.Add));
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Empty_result_backs_off()
    {
        using var clock = new ManualClock();
        var store = NewStore(clock);
        await store.AddSubscriptionAsync("Empty", "https://empty.invalid/list");
        using var handler = new EmptyHandler();
        using var http = new HttpClient(handler);
        var updater = new SubscriptionAutoRefresh(store, new SubscriptionClient(http, new ProfileImporter()), clock);
        var settings = new AppSettings { SubscriptionAutoUpdate = true, SubscriptionUpdateIntervalHours = 12 };

        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, _ => { }));
        Assert.Equal(1, handler.Requests);
        Assert.Equal(0, await updater.RefreshIfDueAsync(settings, false, _ => { }));
        Assert.Equal(1, handler.Requests);
    }

    private static ProfileStore NewStore(TimeProvider clock) => new(
        Path.Combine(TestDirs.New("sub-backoff"), "profiles.json"), new InMemorySecretStore(), clock);

    private sealed class FailingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new HttpRequestException("Fixture failure https://bad.invalid/list token=secret");
        }
    }

    private sealed class EmptyHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(""),
            });
        }
    }

    private sealed class ManualClock : TimeProvider, IDisposable
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private ManualTimer? _timer;
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
            public void Tick(DateTimeOffset now)
            {
                if (now < next) { return; }
                while (next <= now) { next += period; }
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod) => throw new NotSupportedException();
            public void Dispose() { }
            public ValueTask DisposeAsync() { return ValueTask.CompletedTask; }
        }
    }
}
