using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Zapret;
using System.Globalization;
using System.Text.Json;

namespace Bydlo.Tests;

public class EngineDiagnosticTests
{
    [Theory]
    [InlineData("echo no engine", "error.engine.zapret.strategy.executable")]
    [InlineData("winws.exe", "error.engine.zapret.strategy.arguments")]
    public void Invalid_strategy_returns_localizable_diagnostic(string text, string key)
    {
        var error = Assert.Throws<EngineException>(() =>
            ZapretStrategyParser.Parse(text, TestDirs.New("invalid-strategy"), GameFilterValues.From("disabled")));
        Assert.Equal(key, error.LocalizationKey);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void Zapret_translations_cover_diagnostics_and_format_process_names(string language)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "BYDLO.slnx"))) { root = root.Parent; }
        Assert.NotNull(root);
        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(root.FullName, "src", "Bydlo.App", "Assets", "Strings", language + ".json")))!;
        foreach (var suffix in new[] { "conflict", "macos.gamefilter", "strategy.unknown", "strategy.executable", "strategy.arguments", "stopped", "exclusion" })
        {
            var template = strings["error.engine.zapret." + suffix];
            Assert.False(string.IsNullOrWhiteSpace(template));
            var formatted = string.Format(CultureInfo.InvariantCulture, template, "winws, goodbyedpi");
            if (suffix == "conflict") { Assert.Contains("winws, goodbyedpi", formatted, StringComparison.Ordinal); }
            if (language == "ru") { Assert.Matches("[А-Яа-я]", formatted); }
        }
    }

    [Fact]
    public async Task Parameterized_diagnostic_keeps_redacted_arguments_in_session_snapshot()
    {
        var runner = new FakeProcessRunner();
        var redactor = new LogRedactor();
        redactor.RegisterSecret("private-value");
        var registry = new EngineRegistry(() => new ParameterizedEngine(runner));
        await using var manager = new ConnectionManager(registry, new InMemorySecretStore(),
            new RootedAppPaths(TestDirs.New("parameterized-diagnostics")), new ConflictDetector(new FakePorts()),
            new LogHub(redactor), os: OsFamily.Windows);

        var error = await Assert.ThrowsAsync<EngineException>(() =>
            manager.StartZapretAsync(new AppSettings { MockMode = true }));
        var snapshot = manager.Snapshot(ConnectionManager.ZapretKey);
        Assert.Equal(error.LocalizationKey, snapshot.ErrorLocalizationKey);
        Assert.Equal("winws, " + redactor.Redact("private-value"), Assert.Single(snapshot.ErrorLocalizationArguments));
        Assert.Empty(runner.Started);
    }

    private sealed class ParameterizedEngine(FakeProcessRunner runner) : ProcessEngineBase(runner, new LogRedactor())
    {
        public override EngineDescriptor Descriptor => MissingEngineDescriptor;
        private static readonly EngineDescriptor MissingEngineDescriptor = new()
        {
            Kind = EngineKind.Mock, DisplayName = "Diagnostic test", Upstream = "", License = "MIT", IsTool = true,
        };
        public override bool CanHandle(Profile profile) => false;
        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));
        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            throw new EngineException("Another DPI tool is already running (winws, private-value).",
                localizationKey: "error.engine.zapret.conflict", localizationArguments: ["winws, private-value"]);
    }

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
