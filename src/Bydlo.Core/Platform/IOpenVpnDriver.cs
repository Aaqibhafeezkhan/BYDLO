namespace Bydlo.Core.Platform;

public interface IOpenVpnDriver
{
    bool IsInstalled { get; }

    Task InstallAsync(bool userConsent, CancellationToken ct = default);
}
