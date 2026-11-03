using Bydlo.Core.Platform;

namespace Bydlo.Platform;

public sealed class WindowsExitEventFactory : IExitEventFactory
{
    public IExitEvent Create(string name)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        return new ExitEvent(new EventWaitHandle(false, EventResetMode.ManualReset, name));
    }

    private sealed class ExitEvent(EventWaitHandle handle) : IExitEvent
    {
        public void Signal() => handle.Set();
        public void Dispose() => handle.Dispose();
    }
}
