using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Desktop;
using Bydlo.Platform;

namespace Bydlo.Tests;

public class ElevatedRestartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uac_result_controls_mock_session_cleanup(bool accepted)
    {
        var directory = TestDirs.New("elevation-data");
        var registry = new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 });
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("elevation")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        await manager.ConnectAsync(new Profile { Name = "demo", Protocol = ProtocolKind.Vless, Host = "demo.invalid", Port = 443 },
            new AppSettings { MockMode = true });
        var privileges = new RestartPrivileges(args =>
        {
            Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
            Assert.Equal(["--mock", ElevatedRestart.DataDirectoryArgument + directory,
                ElevatedRestart.ParentArgument + "42"], args);
            return accepted;
        });

        Assert.Equal(accepted, await ElevatedRestart.TryAsync(privileges, ["--mock"], 42, manager.StopAllAsync,
            forcedMock: true, dataDirectory: directory));
        Assert.Equal(accepted ? ConnectionStatus.Disconnected : ConnectionStatus.Connected, manager.Primary.Status);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_the_parent_in_control()
    {
        await Assert.ThrowsAsync<IOException>(() => ElevatedRestart.TryAsync(new RestartPrivileges(_ => true),
            ["--mock"], 42, () => throw new IOException("fake cleanup failure"),
            forcedMock: true, dataDirectory: TestDirs.New("elevation-cleanup")));
        var args = new[] { "--mock", ElevatedRestart.ParentArgument + "42" };
        Assert.False(ElevationHandoff.WaitForParent(ref args, _ => false));
        Assert.Equal(["--mock"], args);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void Restart_round_trip_preserves_effective_data_directory_and_forced_mock(bool environmentMock, bool argumentMock)
    {
        var directory = Path.Combine(TestDirs.New("elevation-path"), "данные с пробелами");
        var original = new List<string> { "--elevation-parent=1", "--data-dir=old", "--data-dir=older" };
        if (argumentMock) { original.Add("--mock"); }
        var forcedMock = AppSettings.IsMockForced(original, environmentMock ? "1" : null);
        var launch = ElevatedRestart.BuildArguments(original, 42, forcedMock, directory).ToArray();

        // The elevated process receives arguments only; its environment need not contain BYDLO_MOCK.
        Assert.Equal(environmentMock || argumentMock, AppSettings.IsMockForced(launch, null));
        Assert.Equal(forcedMock ? 1 : 0, launch.Count(a => a == "--mock"));
        Assert.Single(launch, a => a.StartsWith(ElevatedRestart.ParentArgument, StringComparison.Ordinal));
        Assert.Single(launch, a => a.StartsWith(ElevatedRestart.DataDirectoryArgument, StringComparison.Ordinal));
        Assert.True(ElevationHandoff.WaitForParent(ref launch, id => id == 42));
        // Explicit arguments take precedence over the BYDLO_DATA_DIR set by the test environment.
        Assert.Equal(directory, AppPathsFactory.Create(launch).DataDirectory);
        Assert.Equal(directory, AppPathsFactory.Create(ElevatedRestart.BuildArguments(launch, 43, forcedMock, directory)).DataDirectory);
    }

    [Fact]
    public void Safety_lock_refuses_elevation_even_when_called_directly()
    {
        Assert.True(NetworkSafetyGuard.IsLocked);
        var privileges = new PrivilegeService();
        Assert.False(privileges.CanRestartElevated);
        Assert.False(privileges.TryRestartElevated(["--mock"]));
    }

    [Fact]
    public void Empty_explicit_data_directory_cannot_fall_back_to_another_profile_store()
    {
        Assert.Throws<ArgumentException>(() => AppPathsFactory.Create(["--data-dir="]));
    }

    [Theory]
    [InlineData("--elevation-parent=bad")]
    [InlineData("--elevation-parent=0")]
    [InlineData("--elevation-parent=-1")]
    public void Invalid_parent_never_enters_activation_broker(string marker)
    {
        var args = new[] { marker };
        Assert.False(ElevationHandoff.WaitForParent(ref args, _ => throw new InvalidOperationException()));
    }

    [Fact]
    public async Task Windows_broker_handoff_claims_primary_after_parent_releases_it()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var directory = TestDirs.New("elevation-broker");
        using var parentReady = new ManualResetEventSlim();
        using var childWaiting = new ManualResetEventSlim();
        using var stopParent = new ManualResetEventSlim();
        using var parentExited = new ManualResetEventSlim();
        var parentResult = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parent = new Thread(() =>
        {
            try
            {
                using (var broker = new WindowsActivationBroker(directory, _ => throw new InvalidOperationException("Must not forward to parent")))
                {
                    Assert.True(broker.IsPrimary);
                    parentReady.Set();
                    Assert.True(stopParent.Wait(TimeSpan.FromSeconds(5)));
                }
                parentResult.SetResult();
            }
            catch (Exception ex) { parentResult.SetException(ex); }
            finally { parentExited.Set(); }
        });
        parent.Start();
        Assert.True(parentReady.Wait(TimeSpan.FromSeconds(5)));
        var child = Task.Run(() =>
        {
            var args = new[] { "--mock", ElevatedRestart.ParentArgument + "42" };
            Assert.True(ElevationHandoff.WaitForParent(ref args, id =>
            {
                Assert.Equal(42, id);
                childWaiting.Set();
                return parentExited.Wait(TimeSpan.FromSeconds(5));
            }));
            using var broker = new WindowsActivationBroker(directory, _ => { });
            Assert.True(broker.IsPrimary);
            Assert.Equal(["--mock"], args);
        });
        Assert.True(childWaiting.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(child.IsCompleted);
        stopParent.Set();
        await parentResult.Task;
        await child;
    }

    private sealed class RestartPrivileges(Func<IReadOnlyList<string>, bool> launch) : IPrivilegeService
    {
        public bool IsElevated => false;
        public bool CanRestartElevated => true;
        public bool TryRestartElevated(IReadOnlyList<string> args) => launch(args);
    }
}
