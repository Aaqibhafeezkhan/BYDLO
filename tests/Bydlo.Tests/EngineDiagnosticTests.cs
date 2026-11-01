using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

public class EngineDiagnosticTests
{
    [Theory]
    [InlineData("error.engine.zapret.missing")]
    [InlineData(null)]
    public async Task Availability_diagnostic_survives_exception_and_session_snapshot(string? key)
    {
        var runner = new FakeProcessRunner();
        var registry = new EngineRegistry(() => new MissingEngine(runner, key));
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("diagnostics")), new ConflictDetector(new FakePorts()),
            new LogHub(new LogRedactor()), os: OsFamily.Windows);

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            manager.StartZapretAsync(new AppSettings { MockMode = true }));

        Assert.Equal("Missing test engine.", error.Message);
        Assert.Equal(key, error.LocalizationKey);
        var snapshot = manager.Snapshot(ConnectionManager.ZapretKey);
        Assert.Equal(ConnectionStatus.Error, snapshot.Status);
        Assert.Equal(error.Message, snapshot.Error);
        Assert.Equal(key, snapshot.ErrorLocalizationKey);
        Assert.Empty(runner.Started);
        Assert.Empty(runner.Completed);
    }

    private sealed class MissingEngine(FakeProcessRunner runner, string? key)
        : ProcessEngineBase(runner, new LogRedactor())
    {
        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.Mock, DisplayName = "Missing test engine", Upstream = "", License = "MIT", IsTool = true,
        };

        public override bool CanHandle(Profile profile) => false;

        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "Missing test engine.") { LocalizationKey = key });

        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            throw new InvalidOperationException("Unavailable engines must never prepare a process.");
    }
}
