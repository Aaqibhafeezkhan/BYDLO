using System.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Logging;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Tests;

/// <summary>
/// Issue #357: one redaction per line, a background file writer and coalesced UI batches.
/// The observable contract — <c>bydlo.log</c> content, masking and the ring buffer — must not change.
/// </summary>
public sealed class LogHubTests
{
    private const long MaxFileBytes = 2 * 1024 * 1024;

    [Fact]
    public async Task File_keeps_the_same_line_format_as_the_ui()
    {
        var dir = TestDirs.New("log-format");
        using var hub = new LogHub(new LogRedactor(), dir);

        hub.Info("primary", "connected");
        hub.Warn("zapret", "warning line");
        hub.Error("tgwsproxy", "failed");
        await hub.FlushAsync();

        var lines = (await ReadLiveLogAsync(dir)).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(hub.Snapshot().Select(e => e.ToString()), lines);
    }

    [Fact]
    public async Task Secrets_are_masked_in_the_buffer_and_in_the_file()
    {
        var dir = TestDirs.New("log-secret");
        var redactor = new LogRedactor();
        redactor.RegisterSecret("super-secret-token");
        using var hub = new LogHub(redactor, dir);

        hub.Info("primary", "auth with super-secret-token via vless://11111111-2222-3333-4444-555555555555@host:443");
        await hub.FlushAsync();

        var entry = Assert.Single(hub.Snapshot());
        Assert.DoesNotContain("super-secret-token", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", entry.Message, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, entry.Message, StringComparison.Ordinal);

        var file = await ReadLiveLogAsync(dir);
        Assert.DoesNotContain("super-secret-token", file, StringComparison.Ordinal);
        Assert.DoesNotContain("11111111-2222-3333-4444-555555555555", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_lines_are_redacted_at_the_source_and_stored_without_a_second_pass()
    {
        // The engine's redactor knows the secret (as in the helper process); the hub's does not.
        var engineRedactor = new LogRedactor();
        engineRedactor.RegisterSecret("engine-secret-value");
        var runner = new FakeProcessRunner();
        await using var engine = new LogEngine(runner, engineRedactor);
        var emitted = new List<string>();
        engine.Log += (_, line) => { lock (emitted) { emitted.Add(line); } };

        await engine.StartAsync(new EngineStartRequest
        {
            Settings = new AppSettings(),
            WorkDirectory = TestDirs.New("log-engine"),
        });
        runner.Last!.Emit("connecting with engine-secret-value");

        var line = Assert.Single(emitted);
        Assert.Equal("connecting with " + LogRedactor.Mask, line);

        using var hub = new LogHub(new LogRedactor());
        hub.WriteRedacted(LogLevel.Info, "primary", line);
        var stored = Assert.Single(hub.Snapshot()).Message;
        Assert.Equal(line, stored);
        Assert.DoesNotContain("engine-secret-value", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Ring_buffer_keeps_the_newest_entries()
    {
        using var hub = new LogHub(new LogRedactor(), capacity: 10);
        for (var i = 0; i < 25; i++)
        {
            hub.Info("primary", "line " + i);
        }

        var snapshot = hub.Snapshot();
        Assert.Equal(10, snapshot.Count);
        Assert.Equal("line 15", snapshot[0].Message);
        Assert.Equal("line 24", snapshot[^1].Message);
    }

    [Fact]
    public void Clear_removes_entries_of_one_source()
    {
        using var hub = new LogHub(new LogRedactor());
        hub.Info("primary", "a");
        hub.Info("zapret", "b");
        hub.Info("primary", "c");

        hub.Clear("zapret");

        Assert.Equal(new[] { "a", "c" }, hub.Snapshot().Select(e => e.Message));
        Assert.Empty(hub.Snapshot("zapret"));

        hub.Clear();
        Assert.Empty(hub.Snapshot());
    }

    [Fact]
    public async Task Ui_batches_coalesce_many_lines()
    {
        using var hub = new LogHub(new LogRedactor());
        var batches = 0;
        var received = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.EntriesAdded += (_, batch) =>
        {
            Interlocked.Increment(ref batches);
            if (Interlocked.Add(ref received, batch.Count) >= 1000)
            {
                done.TrySetResult();
            }
        };

        for (var i = 0; i < 1000; i++)
        {
            hub.Info("primary", "line " + i);
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // One callback per line would be 1000; the timer must coalesce them into a handful.
        Assert.True(Volatile.Read(ref batches) < 50, $"expected coalesced batches, got {batches}");
    }

    [Fact]
    public void Ui_flush_interval_is_between_100_and_250_ms()
    {
        Assert.InRange(LogHub.UiFlushInterval.TotalMilliseconds, 100, 250);
    }

    [Fact]
    public async Task File_rotates_at_two_megabytes()
    {
        var dir = TestDirs.New("log-rotate");
        using var hub = new LogHub(new LogRedactor(), dir);
        var message = new string('x', 300);

        for (var i = 0; i < 8000; i++)
        {
            hub.Info("primary", message);
        }

        await hub.FlushAsync();

        Assert.True(File.Exists(Path.Combine(dir, "bydlo.log.1")), "the previous file must be kept as bydlo.log.1");
        Assert.True(new FileInfo(Path.Combine(dir, "bydlo.log")).Length <= MaxFileBytes);
    }

    [Fact]
    public async Task Benchmark_shows_at_least_20x_speedup_over_the_per_line_file_append()
    {
        // The issue measured win-x64: on NTFS every File.AppendAllText costs an open/close pair,
        // which is exactly what the background writer removes. On tmpfs the legacy path is cheap
        // enough that the ratio says nothing, so the comparison runs on Windows only.
        // A wall-clock ratio depends on the machine's disk (a CI runner measured 9.8x where a desktop
        // measures well above 20x), so it is a manual benchmark: set BYDLO_RUN_BENCHMARKS=1 to run it.
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("BYDLO_RUN_BENCHMARKS") != "1")
        {
            return;
        }

        const int lines = 5_000;
        var dir = TestDirs.New("log-benchmark");
        var redactor = new LogRedactor();
        redactor.RegisterSecret("benchmark-secret-value");
        const string line = "INFO [sing-box] inbound connection to 127.0.0.1:2080, token=benchmark-secret-value";

        // Reference: the pre-#357 path — redact twice, stat the file, open/append/close it per line.
        var legacyPath = Path.Combine(dir, "legacy.log");
        var legacy = Stopwatch.StartNew();
        for (var i = 0; i < lines; i++)
        {
            var text = redactor.Redact(redactor.Redact(line));
            var info = new FileInfo(legacyPath);
            if (info.Exists && info.Length > MaxFileBytes)
            {
                File.Move(legacyPath, legacyPath + ".1", overwrite: true);
            }

            File.AppendAllText(legacyPath, text + Environment.NewLine);
        }

        legacy.Stop();

        using var hub = new LogHub(redactor, dir);
        var current = Stopwatch.StartNew();
        for (var i = 0; i < lines; i++)
        {
            hub.Info("primary", line);
        }

        current.Stop();
        await hub.FlushAsync();

        var speedup = legacy.Elapsed.TotalMilliseconds / Math.Max(current.Elapsed.TotalMilliseconds, 0.001);
        Assert.True(speedup >= 20, $"expected at least 20x, measured {speedup:0.0}x");
    }

    /// <summary>Reads bydlo.log while the hub still holds it open, as an external reader would.</summary>
    private static async Task<string> ReadLiveLogAsync(string dir)
    {
        await using var stream = new FileStream(Path.Combine(dir, "bydlo.log"), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class LogEngine(IProcessRunner runner, LogRedactor redactor) : ProcessEngineBase(runner, redactor)
    {
        protected override TimeSpan ReadyTimeout => TimeSpan.Zero;

        public override EngineDescriptor Descriptor { get; } = new()
        {
            Kind = EngineKind.SingBox,
            DisplayName = "Log test engine",
            Upstream = "",
            License = "",
            Protocols = new HashSet<ProtocolKind> { ProtocolKind.Trojan },
            Platforms = new Dictionary<OsFamily, SupportLevel> { [OsFamily.Windows] = SupportLevel.Implemented },
        };

        public override bool CanHandle(Profile profile) => true;

        public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
            Task.FromResult(EngineAvailability.Ready("fake"));

        protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct) =>
            Task.FromResult(new ProcessSpec { FileName = "fake" });
    }
}
