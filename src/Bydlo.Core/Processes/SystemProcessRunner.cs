using System.Diagnostics;
using Bydlo.Core.Security;

namespace Bydlo.Core.Processes;

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
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { OutputLine?.Invoke(this, e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { OutputLine?.Invoke(this, e.Data); } };
            process.Exited += (_, _) => RaiseExited();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            journal?.Record(new JournalEntry(spec.DisplayName, Id, Path.GetFileName(spec.FileName), StartedAt,
                GracefulTermination: spec.GracefulTermination, WindowsExitEventName: spec.WindowsExitEventName));
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
                journal?.Remove(Id);
                Exited?.Invoke(this, SafeExitCode());
            }
        }
    }
}
