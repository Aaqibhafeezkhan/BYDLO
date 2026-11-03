using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Bydlo.Desktop;

namespace Bydlo.Tests;

[UnsupportedOSPlatform("windows")]
public sealed class UnixActivationBrokerTests
{
    [UnixFact]
    public async Task Secondary_launch_forwards_imports_and_empty_window_activation()
    {
        var directory = NewDirectory();
        var received = new List<string[]>();
        using var primary = new UnixActivationBroker(directory, args => received.Add(args));
        Assert.True(primary.IsPrimary);
        primary.Listen();
        using (var secondary = new UnixActivationBroker(directory, _ => Assert.Fail("Secondary must not listen")))
        {
            Assert.False(secondary.IsPrimary);
            await secondary.ForwardAsync(["--mock", "--elevation-parent=42", "vless://id@example.invalid:443", "пример.conf"]);
            await secondary.ForwardAsync([]);
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(["vless://id@example.invalid:443", "пример.conf"], received[0]);
        Assert.Empty(received[1]);
        Assert.True(File.Exists(SocketPath(directory)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SocketPath(directory)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(directory, "ipc")));
    }

    [UnixFact]
    public async Task Forward_waits_for_primary_to_bind_its_socket()
    {
        var directory = NewDirectory();
        using var primary = new UnixActivationBroker(directory, _ => { });
        using var secondary = new UnixActivationBroker(directory, _ => { });
        var forwarding = secondary.ForwardAsync([]);
        Assert.False(forwarding.IsCompleted);
        primary.Listen();
        await forwarding.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [UnixFact]
    public async Task Unresponsive_primary_has_bounded_timeout()
    {
        var directory = NewDirectory();
        using var primary = new UnixActivationBroker(directory, _ => { });
        using var secondary = new UnixActivationBroker(directory, _ => { }, TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondary.ForwardAsync([]));
        Assert.True(File.Exists(Path.Combine(directory, "desktop.lock")));
    }

    [UnixFact]
    public async Task Forward_requires_acknowledgement_and_rejects_oversized_input()
    {
        var directory = NewDirectory();
        using var primary = new UnixActivationBroker(directory, _ => { });
        using var server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        server.Bind(new UnixDomainSocketEndPoint(SocketPath(directory)));
        server.Listen(1);
        using var secondary = new UnixActivationBroker(directory, _ => { }, TimeSpan.FromMilliseconds(300));
        var forwarding = secondary.ForwardAsync([]);
        using var client = await server.AcceptAsync();
        using var stream = new NetworkStream(client, ownsSocket: false);
        var request = new byte[6]; // four-byte header and JSON [].
        await stream.ReadExactlyAsync(request);
        Assert.False(forwarding.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forwarding);
        await Assert.ThrowsAsync<IOException>(() => secondary.ForwardAsync([new string('x', 70000) + ".conf"]));
    }

    [UnixFact]
    public async Task Malformed_and_stalled_clients_do_not_stop_listener()
    {
        var directory = NewDirectory();
        var activations = 0;
        using var primary = new UnixActivationBroker(directory, _ => Interlocked.Increment(ref activations),
            TimeSpan.FromMilliseconds(150));
        primary.Listen();
        foreach (var payload in new[]
        {
            Header(-1), Header(UnixActivationBroker.MaxMessageBytes + 1),
            Header(1).Concat(Encoding.UTF8.GetBytes("{")).ToArray(),
            Header(2).Concat(Encoding.UTF8.GetBytes("{}")).ToArray(),
            Header(100), // truncated body
        })
        {
            using var bad = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await bad.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath(directory)));
            using var stream = new NetworkStream(bad, ownsSocket: false);
            await stream.WriteAsync(payload);
        }

        using var stalled = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await stalled.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath(directory)));
        using var secondary = new UnixActivationBroker(directory, _ => { });
        await secondary.ForwardAsync([]).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, activations);
    }

    [UnixFact]
    public void New_primary_replaces_stale_socket_and_retains_lock_inode()
    {
        var directory = NewDirectory();
        var oldLock = Path.Combine(directory, "desktop.lock");
        using (var primary = new UnixActivationBroker(directory, _ => { })) { primary.Listen(); }
        Assert.True(File.Exists(oldLock));
        Assert.False(File.Exists(SocketPath(directory)));
        using var stale = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        stale.Bind(new UnixDomainSocketEndPoint(SocketPath(directory)));
        using var restarted = new UnixActivationBroker(directory, _ => { });
        Assert.True(restarted.IsPrimary);
        restarted.Listen();
        using var secondary = new UnixActivationBroker(directory, _ => { });
        secondary.Forward([]);
    }

    [UnixFact]
    public void Private_directory_is_required_and_symlinks_are_rejected()
    {
        var directory = NewDirectory();
        var ipc = Path.Combine(directory, "ipc");
        Directory.CreateDirectory(ipc);
        File.SetUnixFileMode(ipc, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        Assert.Throws<IOException>(() => new UnixActivationBroker(directory, _ => { }));
        Directory.Delete(ipc);
        var target = Path.Combine(directory, "target");
        Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateSymbolicLink(ipc, target);
        Assert.Throws<IOException>(() => new UnixActivationBroker(directory, _ => { }));
    }

    [UnixFact]
    public async Task Existing_desktop_lock_prevents_a_second_primary()
    {
        var directory = NewDirectory();
        using var legacyLock = new FileStream(Path.Combine(directory, "desktop.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var secondary = new UnixActivationBroker(directory, _ => Assert.Fail("Must not become primary"),
            TimeSpan.FromMilliseconds(150));
        Assert.False(secondary.IsPrimary);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondary.ForwardAsync([]));
    }

    [UnixFact]
    public async Task Different_data_directories_are_independent()
    {
        var first = NewDirectory();
        var second = NewDirectory();
        using var primary = new UnixActivationBroker(first, _ => Assert.Fail("Wrong instance"));
        using var other = new UnixActivationBroker(second, _ => { });
        Assert.True(primary.IsPrimary);
        Assert.True(other.IsPrimary);
        primary.Listen();
        other.Listen();
        using var secondary = new UnixActivationBroker(second, _ => { });
        await secondary.ForwardAsync([]);
    }

    private static byte[] Header(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        return header;
    }

    private static string SocketPath(string directory) => Path.Combine(directory, "ipc", "desktop.sock");

    private static string NewDirectory()
    {
        // Keep below macOS's 104-byte socket path limit, inside the repository's temporary root.
        var path = Path.Combine(Environment.GetEnvironmentVariable("BYDLO_ROOT") ?? Directory.GetCurrentDirectory(),
            ".tmp", "ipc", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) { Skip = "Unix-domain activation is used on Linux and macOS only."; }
    }
}
