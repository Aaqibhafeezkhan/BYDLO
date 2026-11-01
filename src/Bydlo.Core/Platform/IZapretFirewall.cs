using Bydlo.Core.Models;

namespace Bydlo.Core.Platform;

/// <summary>A temporary, private firewall scope. Implementations must journal before applying rules.</summary>
public interface IZapretFirewall
{
    Task ApplyAsync(OsFamily os, string tcpPorts, string udpPorts, IReadOnlyList<string> excludedNetworks, CancellationToken ct = default);

    Task RemoveAsync();
}
