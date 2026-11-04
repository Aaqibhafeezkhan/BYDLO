using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Engines.WireGuard;

namespace Bydlo.Tests;

public class TunnelServiceEngineTests
{
    [Theory]
    [InlineData(OsFamily.Linux, "PreUp")]
    [InlineData(OsFamily.MacOS, "pOsTdOwN")]
    [InlineData(OsFamily.Windows, "PostUp")]
    public async Task Native_engine_rejects_stored_script_hooks_before_launch(OsFamily os, string directive)
    {
        var runner = new FakeProcessRunner();
        await using var engine = CreateEngine(runner, os);
        var request = Request();
        request.Secrets.Values[Keys.RawConfig] += $"\n{directive} = audit-inert-marker";

        var error = await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(request));
        Assert.Equal("error.engine.wireguard.unsafe", error.LocalizationKey);
        Assert.Empty(runner.Completed);
        Assert.Empty(Directory.EnumerateFiles(request.WorkDirectory));
    }

    [Theory]
    [InlineData(OsFamily.Windows, "exit")]
    [InlineData(OsFamily.Windows, "exception")]
    [InlineData(OsFamily.Windows, "cancel")]
    [InlineData(OsFamily.Linux, "exit")]
    [InlineData(OsFamily.Linux, "exception")]
    [InlineData(OsFamily.Linux, "cancel")]
    [InlineData(OsFamily.MacOS, "exit")]
    [InlineData(OsFamily.MacOS, "exception")]
    [InlineData(OsFamily.MacOS, "cancel")]
    public async Task Failed_stop_retains_configuration_and_can_be_retried(OsFamily os, string failure)
    {
        var runner = new FakeProcessRunner();
        await using var engine = CreateEngine(runner, os);
        var request = Request();
        await engine.StartAsync(request);
        var configPath = Path.Combine(request.WorkDirectory, TunnelServiceEngine.InterfaceName + ".conf");
        var states = new List<EngineStateChangedEventArgs>();
        engine.StateChanged += (_, state) => states.Add(state);
        using var cancellation = new CancellationTokenSource();
        runner.OnCompletion = _ => failure switch
        {
            "exit" => (1, "down failed"),
            "exception" => throw new IOException("Stop failed"),
            _ => throw new OperationCanceledException(cancellation.Token),
        };
        cancellation.Cancel();

        var error = await Record.ExceptionAsync(() => engine.StopAsync(cancellation.Token));
        Assert.NotNull(error);
        if (failure == "exit")
        {
            var engineError = Assert.IsType<EngineException>(error);
            Assert.Contains("code 1", engineError.Message);
            Assert.Equal("error.engine.cleanup", engineError.LocalizationKey);
        }
        else if (failure == "exception") { Assert.IsType<IOException>(error); }
        else { Assert.IsType<OperationCanceledException>(error); }
        Assert.Equal(EngineState.Failed, engine.State);
        Assert.Equal(new[] { EngineState.Stopping, EngineState.Failed }, states.Select(s => s.State));
        Assert.False(string.IsNullOrEmpty(states[^1].Error));
        Assert.True(File.Exists(configPath));
        Assert.Equal(request.Secrets[Keys.RawConfig], await File.ReadAllTextAsync(configPath));
        await Assert.ThrowsAsync<EngineException>(() => engine.StartAsync(Request()));
        Assert.Equal(2, runner.Completed.Count);

        var failedStop = runner.Completed[^1];
        Assert.Equal(os == OsFamily.Windows ? "/uninstalltunnelservice" : "down", failedStop.Arguments[0]);
        Assert.Equal(os == OsFamily.Windows ? TunnelServiceEngine.InterfaceName : configPath, failedStop.Arguments[1]);
        runner.OnCompletion = null;
        await engine.StopAsync();
        Assert.Equal(EngineState.Stopped, engine.State);
        Assert.False(File.Exists(configPath));
        Assert.Equal(3, runner.Completed.Count);
        Assert.Equal(failedStop.FileName, runner.Completed[^1].FileName);
        Assert.Equal(failedStop.Arguments, runner.Completed[^1].Arguments);
        await engine.StopAsync();
        Assert.Equal(3, runner.Completed.Count);
    }

    [Fact]
    public async Task Dispose_retries_failed_stop_and_keeps_failed_cleanup_visible_to_manager()
    {
        var runner = new FakeProcessRunner();
        var engine = CreateEngine(runner, OsFamily.Windows);
        var registry = new EngineRegistry(() => throw new InvalidOperationException())
            .Register(engine.Descriptor, () => engine);
        var secrets = new InMemorySecretStore();
        var profile = new Profile { Protocol = ProtocolKind.WireGuard };
        var request = Request();
        await secrets.SaveAsync(profile.Id.ToString(), request.Secrets);
        await using var manager = new ConnectionManager(registry, secrets,
            new RootedAppPaths(TestDirs.New("tunnel-manager")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);
        await manager.ConnectAsync(profile, request.Settings);
        var configPath = runner.Completed[0].Arguments[1];
        runner.CompletionExitCode = 1;

        var error = await Assert.ThrowsAsync<EngineException>(() => manager.DisconnectAsync());
        Assert.Equal("error.engine.cleanup", error.LocalizationKey);
        Assert.Equal(ConnectionStatus.Error, manager.Primary.Status);
        Assert.True(manager.Primary.CleanupPending);
        Assert.True(manager.Primary.HoldsResources);
        Assert.True(File.Exists(configPath));
        Assert.Equal(3, runner.Completed.Count);

        runner.CompletionExitCode = 0;
        await manager.DisconnectAsync();
        Assert.Equal(ConnectionStatus.Disconnected, manager.Primary.Status);
        Assert.False(manager.Primary.CleanupPending);
        Assert.False(File.Exists(configPath));
        Assert.Equal(4, runner.Completed.Count);
    }

    private static TunnelServiceEngine CreateEngine(FakeProcessRunner runner, OsFamily os) =>
        new(false, runner, new LogRedactor(), new FakeLocator(new PlatformInfo(os, CpuArch.X64)), new FakePrivileges(true));

    private static EngineStartRequest Request() => new()
    {
        Settings = new AppSettings(),
        WorkDirectory = TestDirs.New("tunnel-stop"),
        Secrets = new ProfileSecrets { Values = new() { [Keys.RawConfig] = "[Interface]\nPrivateKey = fake-test-key" } },
    };
}
