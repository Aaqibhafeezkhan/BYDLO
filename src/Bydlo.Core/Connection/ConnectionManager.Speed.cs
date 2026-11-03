using System.Net;
using System.Net.Sockets;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Core.Connection;

public sealed partial class ConnectionManager
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IEngine> _failedSpeedProbes = new();

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
        // Custom configurations can route the test URL directly or across multiple outbounds.
        // Their result cannot honestly be attributed to one server.
        if (profile.Protocol is ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig) { return 0; }
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
            DpiDefaults = settings.DpiDefaults,
            Routing = new RoutingSettings { PreserveMeshVpnRanges = false, BypassPrivateNetworks = false },
        };
        var work = Path.Combine(_paths.SessionsDirectory, "speed-" + Guid.NewGuid().ToString("N"));
        var secrets = await _secrets.LoadAsync(profile.Id.ToString(), ct).ConfigureAwait(false);
        var engine = _registry.Create(kind, probeSettings);
        try
        {
            var start = new EngineStartRequest
            {
                Profile = profile,
                Secrets = secrets,
                Settings = probeSettings,
                WorkDirectory = work,
            };

            var httpPort = kind == EngineKind.Xray ? port + 1 : port;
            using var http = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"),
                AllowAutoRedirect = false,
            });
            return await ServerSpeedProbe.MeasureThroughEngineAsync(engine, start, http, target, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (engine.State != EngineState.Stopped)
            {
                // Retain the handle and recovery files until shutdown can retry cleanup.
                _failedSpeedProbes[work] = engine;
            }
            else if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
    }

    private async Task RetrySpeedProbeCleanupAsync(List<Exception> errors)
    {
        foreach (var (work, engine) in _failedSpeedProbes.ToArray())
        {
            try
            {
                await ServerSpeedProbe.StopEngineAsync(engine).ConfigureAwait(false);
                if (Directory.Exists(work)) { Directory.Delete(work, recursive: true); }
                _failedSpeedProbes.TryRemove(work, out _);
            }
            catch (Exception ex) { errors.Add(ex); }
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
