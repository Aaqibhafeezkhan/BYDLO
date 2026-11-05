using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Engines;

namespace Bydlo.Tests;

public sealed class HelperCleanupRetryTests
{
    [Fact]
    public async Task StopFailure_KeepsLeaseUntilRetrySucceeds()
    {
        var root = TestDirs.New("helper-cleanup-stop");
        var pool = new HelperLeasePool();
        var engine = new FlakyStopEngine(failStops: 1);
        await using var session = new HelperSession(_ => engine, root, pool);

        await session.DispatchAsync(StartRequest(), CancellationToken.None);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, engine.StopAttempts);
        Assert.True(Directory.Exists(engine.WorkDirectory));
        Assert.Throws<InvalidDataException>(() => pool.Acquire(EngineKind.SingBox));

        await session.DisposeAsync();
        Assert.Equal(2, engine.StopAttempts);
        Assert.True(engine.Disposed);
        Assert.Empty(Directory.EnumerateDirectories(root));

        using var next = pool.Acquire(EngineKind.SingBox);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task DirectoryFailure_ReleasesLeaseAndRetriesDelete()
    {
        var root = TestDirs.New("helper-cleanup-dir");
        var pool = new HelperLeasePool();
        var engine = new FlakyStopEngine(failStops: 0);
        var deleteAttempts = 0;
        Task DeleteOnceAsync(string directory)
        {
            deleteAttempts++;
            if (deleteAttempts == 1) { throw new IOException("transient delete failure"); }
            Directory.Delete(directory, true);
            return Task.CompletedTask;
        }

        await using var session = new HelperSession(_ => engine, root, pool, DeleteOnceAsync);

        await session.DispatchAsync(StartRequest(), CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() => session.DisposeAsync().AsTask());
        Assert.Equal(1, engine.StopAttempts);
        Assert.Equal(1, deleteAttempts);
        Assert.True(Directory.Exists(engine.WorkDirectory));

        using (pool.Acquire(EngineKind.SingBox)) { }

        await session.DisposeAsync();
        Assert.Equal(2, deleteAttempts);
        Assert.Empty(Directory.EnumerateDirectories(root));

        await session.DisposeAsync();
    }

    private static HelperRequest StartRequest() => new()
    {
        Engine = EngineKind.SingBox,
        Operation = HelperOperation.Start,
        Start = new EngineStartRequest
        {
            Profile = new Profile { Protocol = ProtocolKind.Trojan, Host = "example.org", Port = 443 },
            Secrets = new ProfileSecrets { Values = new() { [Keys.Password] = "secret" } },
            Settings = new AppSettings { Mode = ConnectionMode.Tun },
            WorkDirectory = "untrusted-client-path",
        },
    };

    private sealed class FlakyStopEngine(int failStops) : IEngine
    {
        public int StopAttempts { get; private set; }
        public bool Disposed { get; private set; }
        public string? WorkDirectory { get; private set; }
        public EngineDescriptor Descriptor => Bydlo.Engines.SingBox.SingBoxEngine.Info;
        public EngineState State { get; private set; }
        public event EventHandler<EngineStateChangedEventArgs>? StateChanged { add { } remove { } }
        public event EventHandler<string>? Log { add { } remove { } }
        public event EventHandler<TrafficSample>? Traffic { add { } remove { } }
        public bool CanHandle(Profile profile) => true;
        public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        public Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
        {
            WorkDirectory = request.WorkDirectory;
            State = EngineState.Running;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            StopAttempts++;
            if (StopAttempts <= failStops) { throw new InvalidOperationException("stop boom"); }
            State = EngineState.Stopped;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
