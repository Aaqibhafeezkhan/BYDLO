using System.Net;
using System.Net.Sockets;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Connection;

public sealed partial class ConnectionManager
{
    public Task<IReadOnlyDictionary<Guid, long>> MeasureServerSpeedsAsync(
        IReadOnlyList<Profile> profiles, Uri target, AppSettings settings, CancellationToken ct = default)
    {
        NetworkSafetyGuard.EnsureAllowed("measure server speed");
        if (settings.MockMode)
        {
            throw new EngineException("connections.speed.unavailable", localizationKey: "connections.speed.unavailable");
        }
        return new ServerSpeedProbe((profile, uri, token) => MeasureThroughProxyAsync(profile, uri, settings, token))
            .RunAsync(profiles, target, ct);
    }

    private async Task<long> MeasureThroughProxyAsync(Profile profile, Uri target, AppSettings settings, CancellationToken ct)
    {
        var candidates = _registry.CandidatesFor(profile, _os);
        var kind = profile.PreferredEngine is { } preferred && candidates.Contains(preferred)
            ? preferred : candidates.Count > 0 ? candidates[0] : EngineKind.Mock;
        if (kind is not (EngineKind.SingBox or EngineKind.Xray))
        {
            return 0;
        }

        var port = FindProbePort(kind == EngineKind.Xray);
        var probeSettings = new AppSettings
        {
            EnginePaths = new Dictionary<string, string>(settings.EnginePaths, StringComparer.OrdinalIgnoreCase),
            Mode = ConnectionMode.LocalProxy,
            LocalProxyPort = port,
            Routing = new RoutingSettings { PreserveMeshVpnRanges = false, BypassPrivateNetworks = false },
        };
        var work = Path.Combine(_paths.SessionsDirectory, "speed-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new EngineStartRequest
            {
                Profile = profile,
                Secrets = await _secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false),
                Settings = probeSettings,
                WorkDirectory = work,
            };

            var httpPort = kind == EngineKind.Xray ? port + 1 : port;
            using var http = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
                AllowAutoRedirect = false,
            });
            return await ServerSpeedProbe.MeasureThroughEngineAsync(_registry.Create(kind, probeSettings), start, http, target, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private static int FindProbePort(bool adjacentHttpPort)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            using var socks = new TcpListener(IPAddress.Loopback, 0);
            socks.Start();
            var port = ((IPEndPoint)socks.LocalEndpoint).Port;
            if (!adjacentHttpPort)
            {
                return port;
            }
            if (port < 65535)
            {
                using var http = new TcpListener(IPAddress.Loopback, port + 1);
                try
                {
                    http.Start();
                    return port;
                }
                catch (SocketException)
                {
                    // Choose another pair if the neighboring port is occupied.
                }
            }
        }
        throw new EngineException("No free loopback ports are available.");
    }
}
