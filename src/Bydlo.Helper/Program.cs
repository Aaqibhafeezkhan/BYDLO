using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Runtime.InteropServices;
using Bydlo.Core.Platform;
using Bydlo.Core.Security;
using Bydlo.Platform;

namespace Bydlo.Helper;

internal static class Program
{
    public static int Main(string[] args)
    {
        // No installation, service start, engine or IPC endpoint in a development/mock process.
        NetworkSafetyGuard.EnsureAllowed("run or manage the privileged helper");
        if (Environment.GetEnvironmentVariable("BYDLO_MOCK") == "1" || args.Contains("--mock", StringComparer.Ordinal)) { return 2; }
        if (OperatingSystem.IsWindows()) { return RunWindows(args); }
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) { return 3; }
        if (!Environment.IsPrivilegedProcess) { return 4; }
        if (args.Length == 2 && args[0] == "install") { UnixHelperInstaller.Install(args[1]); return 0; }
        if (args.Length == 1 && args[0] == "remove") { UnixHelperInstaller.Remove(); return 0; }
        if (args.Length != 1 || args[0] != "service") { return 5; }
        using var stop = new CancellationTokenSource();
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); });
        HelperServer.RunAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string[] args)
    {
        if (!Environment.IsPrivilegedProcess) { return 4; }
        if (args.Length == 2 && args[0] == "install") { HelperInstaller.Install(args[1]); return 0; }
        if (args.Length == 1 && args[0] == "remove") { HelperInstaller.Remove(); return 0; }
        if (args.Length != 1 || args[0] != "service") { return 5; }
        using var service = new HelperWindowsService();
        ServiceBase.Run(service);
        return 0;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class HelperWindowsService : ServiceBase
{
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    public HelperWindowsService() { ServiceName = HelperConnection.ServiceName; CanStop = true; }
    protected override void OnStart(string[] args) => _task = HelperServer.RunAsync(_stop.Token);
    protected override void OnStop()
    {
        _stop.Cancel();
        RequestAdditionalTime(30000);
        _task?.GetAwaiter().GetResult();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _stop.Dispose(); }
        base.Dispose(disposing);
    }
}
