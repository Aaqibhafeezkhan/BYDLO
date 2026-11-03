using Bydlo.Core.Engines;
using Bydlo.Core.Models;

namespace Bydlo.Engines.Zapret;

internal interface IZapretBackend
{
    Task<ParsedStrategy> PrepareAsync(ZapretInstallation install, string strategy, ZapretSettings settings, CancellationToken ct);
}

internal sealed class WinDivertBackend : IZapretBackend
{
    public async Task<ParsedStrategy> PrepareAsync(ZapretInstallation install, string strategy, ZapretSettings settings, CancellationToken ct)
    {
        var text = await File.ReadAllTextAsync(Path.Combine(install.Root, strategy), ct).ConfigureAwait(false);
        return ZapretStrategyParser.Parse(text, install.Root, GameFilterValues.From(settings.GameFilter), install.UserListsDirectory);
    }
}

internal sealed class NfqueueBackend : IZapretBackend
{
    public Task<ParsedStrategy> PrepareAsync(ZapretInstallation install, string strategy, ZapretSettings settings, CancellationToken ct) =>
        Task.FromResult(new ParsedStrategy(NativeZapretStrategy.Build(OsFamily.Linux, install, strategy, settings.GameFilter), []));
}

internal sealed class PfTpwsBackend : IZapretBackend
{
    public Task<ParsedStrategy> PrepareAsync(ZapretInstallation install, string strategy, ZapretSettings settings, CancellationToken ct)
    {
        // Backups can carry a Windows/Linux game filter. Keep the saved preference, but
        // ignore it here: tpws and the macOS PF rules only handle IPv4 TCP 80/443.
        return Task.FromResult(new ParsedStrategy(NativeZapretStrategy.Build(OsFamily.MacOS, install, strategy, "disabled"), []));
    }
}
