namespace Bydlo.Core.Platform;

public interface IExitEvent : IDisposable
{
    void Signal();
}

/// <summary>Owns the Windows OpenVPN service-exit event for the lifetime of a session.</summary>
public interface IExitEventFactory
{
    IExitEvent Create(string name);
}
