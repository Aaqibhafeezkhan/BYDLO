using System.Diagnostics;
using System.Text.Json;

namespace Bydlo.Core.Processes;

public sealed record JournalEntry(string DisplayName, int Pid, string ImageName, DateTimeOffset StartedAt);

/// <summary>
/// Persists PIDs of engine processes so that, after a BYDLO crash, orphaned engines can be found and stopped
/// on the next start (crash recovery). Only processes whose image name and start time match are touched.
/// </summary>
public sealed class ProcessJournal(string filePath)
{
    private readonly Lock _gate = new();

    public void Record(JournalEntry entry)
    {
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
    public IReadOnlyList<JournalEntry> FindOrphans()
    {
        var alive = new List<JournalEntry>();
        foreach (var e in Entries())
        {
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
    public int KillOrphans()
    {
        var killed = 0;
        foreach (var e in FindOrphans())
        {
            try
            {
                using var p = Process.GetProcessById(e.Pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            Remove(e.Pid);
        }

        return killed;
    }

    private List<JournalEntry> ReadUnsafe()
    {
        try
        {
            return File.Exists(filePath)
                ? JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(filePath)) ?? []
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
            File.WriteAllText(filePath, JsonSerializer.Serialize(list));
        }
        catch (IOException)
        {
        }
    }
}
