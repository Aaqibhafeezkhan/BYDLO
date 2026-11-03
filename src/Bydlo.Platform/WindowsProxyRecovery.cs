using System.Text.Json;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;

namespace Bydlo.Platform;

internal sealed record WindowsProxyValue(string? Text, int? Number, bool Expand = false);

internal interface IWindowsProxySettings
{
    WindowsProxyValue? Read(string name);
    void Write(string name, WindowsProxyValue? value);
    void Notify();
}

// The native registry adapter is never used in isolated tests.
internal sealed class WindowsProxyRecovery(IWindowsProxySettings settings, string directory,
    Action? checkSafety = null) : ISystemProxyService, IDisposable
{
    private readonly object _gate = new();
    private FileStream? _lease;
    private string Journal => Path.Combine(directory, "windows-system-proxy.json");
    public bool IsSupported => true;

    public Task SetAsync(string host, int port, CancellationToken ct = default)
    {
        EnsureAllowed();
        if (host != "127.0.0.1" || port is < 1 or > 65535)
        {
            throw new ArgumentException("Only a valid loopback proxy is supported.");
        }
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            Acquire();
            try
            {
                Restore(ct);
                var changes = new[]
                {
                    new Change("ProxyServer", settings.Read("ProxyServer"), new($"{host}:{port}", null)),
                    new Change("ProxyOverride", settings.Read("ProxyOverride"), new("localhost;127.*;10.*;172.16.*;192.168.*;<local>", null)),
                    new Change("ProxyEnable", settings.Read("ProxyEnable"), new(null, 1)),
                };
                // Publish a complete durable journal before the first registry mutation.
                using (var stream = new FileStream(Journal + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new ProxyJournal(1, changes));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(Journal + ".tmp", Journal, overwrite: true);
                foreach (var change in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    settings.Write(change.Name, change.Applied);
                }
                settings.Notify();
            }
            catch
            {
                try { Restore(CancellationToken.None); }
                finally { Release(); }
                throw;
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!File.Exists(Journal)) { Release(); return Task.CompletedTask; }
            EnsureAllowed();
            ct.ThrowIfCancellationRequested();
            Acquire();
            try { Restore(ct); }
            finally { Release(); }
        }
        return Task.CompletedTask;
    }

    private void Restore(CancellationToken ct)
    {
        if (!File.Exists(Journal)) { return; }
        ProxyJournal? journal;
        using (var stream = File.OpenRead(Journal))
        {
            journal = JsonSerializer.Deserialize<ProxyJournal>(stream);
        }
        if (journal is not { Version: 1, Changes.Length: 3 } ||
            journal.Changes.Any(c => c is null) ||
            !journal.Changes.Select(c => c.Name).SequenceEqual(["ProxyServer", "ProxyOverride", "ProxyEnable"]) ||
            journal.Changes.Any(c => !Valid(c.Previous) || !Valid(c.Applied) || c.Applied is null))
        {
            throw new InvalidDataException("Invalid Windows proxy recovery journal.");
        }
        var server = journal.Changes[0];
        var currentServer = settings.Read(server.Name);
        var foreignServer = currentServer != server.Applied && currentServer != server.Previous;
        // A new endpoint also transfers ownership of the enable flag to the other app.
        // Disable/restore the enable flag first; each write is independently retryable.
        foreach (var change in journal.Changes.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (!(foreignServer && change.Name == "ProxyEnable") && settings.Read(change.Name) == change.Applied)
            {
                settings.Write(change.Name, change.Previous);
            }
        }
        // Keep the journal if notification fails, even after registry restoration.
        settings.Notify();
        File.Delete(Journal);
    }

    private static bool Valid(WindowsProxyValue? value) => value is null ||
        (value.Number is not null && value.Text is null && !value.Expand) ||
        (value.Number is null && value.Text is not null);

    private void EnsureAllowed()
    {
        if (checkSafety is not null) { checkSafety(); }
        else { NetworkSafetyGuard.EnsureAllowed("change the system proxy"); }
    }

    private void Acquire()
    {
        Directory.CreateDirectory(directory);
        _lease ??= new FileStream(Path.Combine(directory, "windows-system-proxy.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private void Release() { _lease?.Dispose(); _lease = null; }
    public void Dispose() { lock (_gate) { Release(); } }

    private sealed record ProxyJournal(int Version, Change[] Changes);
    private sealed record Change(string Name, WindowsProxyValue? Previous, WindowsProxyValue? Applied);
}
