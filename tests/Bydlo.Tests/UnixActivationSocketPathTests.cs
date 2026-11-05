using System.Runtime.Versioning;
using System.Text;
using Bydlo.Desktop;

namespace Bydlo.Tests;

/// <summary>Issue #341: long BYDLO_DATA_DIR must not exceed the Unix sun_path limit.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class UnixActivationSocketPathTests
{
    [Fact]
    public void Long_data_dir_yields_short_socket_path()
    {
        // No real directory needed: ResolveSocketPath is pure (issue #341).
        var longDir = "/very/long/data/dir/" + new string('x', 130);
        Assert.True(longDir.Length > 130);

        var socketPath = UnixActivationBroker.ResolveSocketPath(longDir);

        Assert.EndsWith("desktop.sock", socketPath, StringComparison.Ordinal);
        Assert.True(Encoding.UTF8.GetByteCount(socketPath) < 100,
            $"Socket path must stay below the sun_path limit, was {Encoding.UTF8.GetByteCount(socketPath)} bytes: {socketPath}");
        Assert.True(socketPath.Length < 80,
            $"Socket path must stay below 80 chars, was {socketPath.Length}: {socketPath}");
        Assert.DoesNotContain(new string('x', 32), socketPath, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveSocketPath_is_deterministic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bydlo-341-" + Guid.NewGuid().ToString("N")[..8]);
        var first = UnixActivationBroker.ResolveSocketPath(dir);
        var second = UnixActivationBroker.ResolveSocketPath(dir);
        Assert.Equal(first, second);

        // Trailing separators normalize to the same socket (primary/secondary stability).
        Assert.Equal(first, UnixActivationBroker.ResolveSocketPath(dir + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Different_data_directories_yield_different_sockets()
    {
        var first = UnixActivationBroker.ResolveSocketPath(
            Path.Combine(Path.GetTempPath(), "bydlo-341-" + Guid.NewGuid().ToString("N")[..8]));
        var second = UnixActivationBroker.ResolveSocketPath(
            Path.Combine(Path.GetTempPath(), "bydlo-341-" + Guid.NewGuid().ToString("N")[..8]));
        Assert.NotEqual(first, second);
    }

    [UnixFact]
    public async Task Broker_listen_forward_works_with_long_data_dir()
    {
        var directory = NewLongDirectory();
        Assert.True(directory.Length > 130, $"Test dir must exceed 130 chars, was {directory.Length}");

        var socketPath = UnixActivationBroker.ResolveSocketPath(directory);
        Assert.True(Encoding.UTF8.GetByteCount(socketPath) < 100);

        var received = new List<string[]>();
        using var primary = new UnixActivationBroker(directory, args => received.Add(args));
        Assert.True(primary.IsPrimary);
        primary.Listen();

        using (var secondary = new UnixActivationBroker(directory, _ => Assert.Fail("Secondary must not listen")))
        {
            Assert.False(secondary.IsPrimary);
            Assert.Equal(socketPath, UnixActivationBroker.ResolveSocketPath(directory));
            await secondary.ForwardAsync(["vless://id@example.invalid:443"]);
        }

        Assert.Single(received);
        Assert.Equal(["vless://id@example.invalid:443"], received[0]);
        Assert.True(File.Exists(Path.Combine(directory, "desktop.lock")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(socketPath)!));
    }

    private static string NewLongDirectory()
    {
        var root = Environment.GetEnvironmentVariable("BYDLO_ROOT") ?? Directory.GetCurrentDirectory();
        // Single 120-char component keeps filename <255 while total path exceeds 130 chars.
        var path = Path.Combine(root, ".tmp", "long-" + new string('d', 120) + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }
}
