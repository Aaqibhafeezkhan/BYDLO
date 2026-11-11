using Bydlo.Core.Processes;
using System.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.Platform.Processes;

/// <summary>Real process runner. Refuses network-affecting processes while the safety lock is active.</summary>
public sealed class SystemProcessRunner(ProcessJournal? journal = null) : IProcessRunner
{
    public IManagedProcess Start(ProcessSpec spec)
    {
        if (spec.AffectsNetwork)
        {
            NetworkSafetyGuard.EnsureAllowed($"start {spec.DisplayName}");
        }

        var psi = CreateStartInfo(spec);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var managed = new SystemManagedProcess(process, spec, journal);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {spec.DisplayName}.");
        }

        managed.AttachAfterStart();
        return managed;
    }

    public async Task<(int ExitCode, string Output)> RunToCompletionAsync(ProcessSpec spec, TimeSpan timeout, CancellationToken ct = default)
    {
        if (spec.AffectsNetwork)
        {
            NetworkSafetyGuard.EnsureAllowed($"run {spec.DisplayName}");
        }

        using var process = new Process { StartInfo = CreateStartInfo(spec) };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (output) { output.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{spec.DisplayName} did not finish in {timeout.TotalSeconds:0}s.");
        }

        // Flush asynchronous output callbacks before inspecting the structured result.
        process.WaitForExit();

        lock (output)
        {
            return (process.ExitCode, output.ToString());
        }
    }

    /// <summary>
    /// Records a newly started process. If the journal write fails, the process would
    /// otherwise run untracked (no crash-recovery entry), so kill it and fail closed
    /// instead of leaking an engine the journal cannot find later (issue #343).
    /// </summary>
    internal static void RecordOrCleanup(Process process, ProcessJournal? journal, JournalEntry entry)
    {
        try
        {
            journal?.Record(entry);
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
                // Already exited or not killable: the Record exception below is the
                // one the caller must see, so never mask it with cleanup noise.
            }
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo(ProcessSpec spec)
    {
        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = spec.WorkingDirectory ?? Path.GetDirectoryName(spec.FileName) ?? "",
        };
        foreach (var a in spec.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in spec.Environment)
        {
            psi.Environment[k] = v;
        }

        return psi;
    }

    private sealed class SystemManagedProcess(Process process, ProcessSpec spec, ProcessJournal? journal) : IManagedProcess
    {
        private int _exitRaised;
        private int _deliveryBegun;

        public int Id { get; private set; }

        public DateTimeOffset StartedAt { get; private set; }

        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode => HasExited ? SafeExitCode() : null;

        public event EventHandler<string>? OutputLine;

        public event EventHandler<int>? Exited;

        public void AttachAfterStart()
        {
            Id = process.Id;
            StartedAt = DateTimeOffset.Now;
            RecordOrCleanup(process, journal, new JournalEntry(spec.DisplayName, Id, Path.GetFileName(spec.FileName), StartedAt,
                GracefulTermination: spec.GracefulTermination, WindowsExitEventName: spec.WindowsExitEventName));
        }

        public void BeginEventDelivery()
        {
            if (Interlocked.Exchange(ref _deliveryBegun, 1) != 0) { return; }
            // Reading starts only after the consumer subscribed; earlier output waits in the pipe (issue #468).
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { OutputLine?.Invoke(this, e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { OutputLine?.Invoke(this, e.Data); } };
            process.Exited += (_, _) => RaiseExited();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (process.HasExited)
            {
                RaiseExited();
            }
        }

        public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            if (HasExited)
            {
                journal?.Remove(Id);
                return;
            }

            try
            {
                if (spec.GracefulTermination) { ProcessTermination.RequestGracefulExit(process, spec.WindowsExitEventName); }
                // Closing stdin lets well-behaved CLIs shut down; then wait, then kill the tree.
                process.StandardInput.Close();
            }
            catch (InvalidOperationException) { }
            catch (IOException) { }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            journal?.Remove(Id);
        }

        public async ValueTask DisposeAsync()
        {
            if (!HasExited)
            {
                await StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }

            process.Dispose();
        }

        private int SafeExitCode()
        {
            try { return process.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        }

        private void RaiseExited()
        {
            if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
            {
                try
                {
                    journal?.Remove(Id);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A void process-exit callback must not throw. The stale entry stays
                    // in the journal and the next FindOrphans fails closed on the same
                    // I/O error instead of silently dropping it (issue #343).
                    Debug.Fail($"Process journal cleanup failed: {ex.Message}");
                }
                Exited?.Invoke(this, SafeExitCode());
            }
        }
    }
}
