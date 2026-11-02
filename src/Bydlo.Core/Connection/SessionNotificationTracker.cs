using Bydlo.Core.Models;

namespace Bydlo.Core.Connection;

/// <summary>Filters traffic updates and intentional stops out of desktop notifications.</summary>
public sealed class SessionNotificationTracker
{
    private readonly Dictionary<string, ConnectionStatus> _statuses = new(StringComparer.Ordinal);
    private readonly HashSet<string> _lost = new(StringComparer.Ordinal);

    public string? Update(SessionSnapshot session)
    {
        var previous = _statuses.GetValueOrDefault(session.Key, ConnectionStatus.Disconnected);
        _statuses[session.Key] = session.Status;
        if (previous == session.Status) { return null; }
        if (session.Status is ConnectionStatus.Disconnected or ConnectionStatus.Disconnecting)
        {
            _lost.Remove(session.Key);
            return null;
        }
        if (session.Status == ConnectionStatus.Reconnecting)
        {
            return _lost.Add(session.Key) ? "notification.lost" : null;
        }
        if (session.Status == ConnectionStatus.Error)
        {
            if (previous == ConnectionStatus.Connected) { _lost.Add(session.Key); }
            return "notification.error";
        }
        if (session.Status == ConnectionStatus.Connected && _lost.Remove(session.Key)) { return "notification.restored"; }
        return null;
    }
}
