using Bydlo.Core.Connection;
using Bydlo.Core.Models;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Opens Telegram only after the proxy is ready, using its active credentials.</summary>
public sealed class TelegramProxyConnection(ConnectionManager connections, TgWsProxySecrets secrets)
{
    public async Task<bool> OpenAsync(AppSettings settings, Func<Uri, Task<bool>> launch, CancellationToken ct = default)
    {
        await secrets.GetOrCreateAsync(ct);
        var (active, secret) = await connections.EnsureTgWsProxyAsync(settings, ct);
        var link = TgLinkBuilder.Build(active.Host, active.Port, secret, active.FakeTlsDomain);
        return await launch(new Uri(link));
    }
}
