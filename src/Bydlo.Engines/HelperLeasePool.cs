using Bydlo.Core.Models;

namespace Bydlo.Engines;

/// <summary>There can be one primary privileged tunnel and one independent DPI tool.</summary>
public sealed class HelperLeasePool
{
    private readonly HashSet<bool> _leases = [];
    public IDisposable Acquire(EngineKind kind)
    {
        var tool = kind == EngineKind.Zapret;
        lock (_leases)
        {
            if (!_leases.Add(tool)) { throw new InvalidDataException("A privileged engine already owns this resource."); }
        }
        return new Lease(this, tool);
    }

    private sealed class Lease(HelperLeasePool pool, bool tool) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) { lock (pool._leases) { pool._leases.Remove(tool); } }
        }
    }
}
