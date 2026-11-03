using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Engines.Mock;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Desktop;

namespace Bydlo.Tests;

public class ElevatedRestartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uac_result_controls_mock_session_cleanup(bool accepted)
    {
        var registry = new EngineRegistry(() => new MockEngine(seed: 1) { TimeScale = 0.01 });
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("elevation")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        await manager.ConnectAsync(new Profile { Name = "demo", Protocol = ProtocolKind.Vless, Host = "demo.invalid", Port = 443 },
            new AppSettings { MockMode = true });
        var privileges = new RestartPrivileges(args =>
        {
            Assert.Equal(ConnectionStatus.Connected, manager.Primary.Status);
            Assert.Equal(["--mock", ElevatedRestart.ParentArgument + "42"], args);
            return accepted;
        });

        Assert.Equal(accepted, await ElevatedRestart.TryAsync(privileges, ["--mock"], 42, manager.StopAllAsync));
        Assert.Equal(accepted ? ConnectionStatus.Disconnected : ConnectionStatus.Connected, manager.Primary.Status);
    }

    [Fact]
    public async Task Cleanup_failure_keeps_the_parent_in_control()
    {
        await Assert.ThrowsAsync<IOException>(() => ElevatedRestart.TryAsync(new RestartPrivileges(_ => true),
            ["--mock"], 42, () => throw new IOException("fake cleanup failure")));
        var args = new[] { "--mock", ElevatedRestart.ParentArgument + "42" };
        Assert.False(ElevationHandoff.WaitForParent(ref args, _ => false));
        Assert.Equal(["--mock"], args);
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
