using System.Diagnostics;
using System.Text.Json;
using Bydlo.Core.Security;

namespace Bydlo.Platform.Processes;

public sealed record JournalEntry(string DisplayName, int Pid, string ImageName, DateTimeOffset StartedAt,
    int OwnerPid = 0, DateTimeOffset? OwnerStartedAt = null, bool GracefulTermination = false, string? WindowsExitEventName = null);

/// <summary>
/// Persists PIDs of engine processes so that, after a BYDLO crash, orphaned engines can be found and stopped
/// on the next start (crash recovery). Only processes whose image name and start time match are touched.
/// </summary>
public class ProcessJournal(string filePath)
{
    private readonly Lock _gate = new();

    public void Record(JournalEntry entry)
    {
        using var owner = Process.GetCurrentProcess();
        entry = entry with { OwnerPid = Environment.ProcessId, OwnerStartedAt = new DateTimeOffset(owner.StartTime) };
        lock (_gate)
        {
            var list = ReadUnsafe();
            list.RemoveAll(e => e.Pid == entry.Pid);
            list.Add(entry);
            WriteUnsafe(list);
        }
    }

    public void Remove(int pid)
    {
        lock (_gate)
        {
            var list = ReadUnsafe();
            if (list.RemoveAll(e => e.Pid == pid) > 0)
            {
                WriteUnsafe(list);
            }
        }
    }

    public IReadOnlyList<JournalEntry> Entries()
    {
        lock (_gate)
        {
            return ReadUnsafe();
        }
    }

    /// <summary>Returns journal entries whose process is still alive and matches the recorded image.</summary>
    public virtual IReadOnlyList<JournalEntry> FindOrphans()
    {
        var alive = new List<JournalEntry>();
        foreach (var e in Entries())
        {
            if (HasOtherLiveOwner(e)) { continue; }
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                var image = Path.GetFileNameWithoutExtension(e.ImageName);
                var startedNear = Math.Abs((new DateTimeOffset(p.StartTime) - e.StartedAt).TotalSeconds) < 10;
                if (p.ProcessName.Equals(image, StringComparison.OrdinalIgnoreCase) && startedNear)
                {
                    alive.Add(e);
                }
                else
                {
                    Remove(e.Pid);
                }
            }
            catch (ArgumentException)
            {
                Remove(e.Pid);
            }
            catch (InvalidOperationException)
            {
                Remove(e.Pid);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Access denied: leave it to the user.
                alive.Add(e);
            }
        }

        return alive;
    }

    /// <summary>Kills orphaned engines left after a crash.</summary>
    public virtual int KillOrphans(string? displayName = null)
    {
        NetworkSafetyGuard.EnsureAllowed("clean up orphaned network engines");
        var killed = 0;
        foreach (var e in FindOrphans())
        {
            if (displayName is not null && !string.Equals(e.DisplayName, displayName, StringComparison.Ordinal)) { continue; }
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                // Recheck on the handle used for termination, in case the PID was recycled.
                if (!p.ProcessName.Equals(Path.GetFileNameWithoutExtension(e.ImageName), StringComparison.OrdinalIgnoreCase)
                    || Math.Abs((new DateTimeOffset(p.StartTime) - e.StartedAt).TotalSeconds) >= 10)
                {
                    Remove(e.Pid);
                    continue;
                }
                if (!e.GracefulTermination || !ProcessTermination.RequestGracefulExit(p, e.WindowsExitEventName) || !p.WaitForExit(5000))
                {
                    p.Kill(entireProcessTree: true);
                }
                if (p.WaitForExit(5000))
                {
                    killed++;
                    Remove(e.Pid);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                Remove(e.Pid);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Preserve failed cleanup for a later elevated recovery attempt.
            }
        }

        return killed;
    }

    private static bool HasOtherLiveOwner(JournalEntry entry)
    {
        if (entry.OwnerPid == 0 || entry.OwnerPid == Environment.ProcessId || entry.OwnerStartedAt is null) { return false; }
        try
        {
            using var owner = Process.GetProcessById(entry.OwnerPid);
            return !owner.HasExited && Math.Abs((new DateTimeOffset(owner.StartTime) - entry.OwnerStartedAt.Value).TotalSeconds) < 1;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Access denied: leave a potentially live owner alone.
    }

    private List<JournalEntry> ReadUnsafe()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize(File.ReadAllText(filePath), PlatformJsonContext.Default.ListJournalEntry) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    private void WriteUnsafe(List<JournalEntry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(list, PlatformJsonContext.Default.ListJournalEntry));
        }
        catch (IOException)
        {
        }
    }
}
