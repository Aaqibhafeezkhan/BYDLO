using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Bydlo.Core.Config;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Platform;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;

namespace Bydlo.Engines.SingBox;

/// <summary>sing-box adapter: VLESS/REALITY, VMess, Trojan, Shadowsocks, Hysteria 2, TUIC, SOCKS/HTTP, WireGuard.</summary>
public sealed class SingBoxEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, IPrivilegeService privileges)
    : ProcessEngineBase(runner, redactor)
{
    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.SingBox,
        DisplayName = "sing-box",
        Upstream = "https://github.com/SagerNet/sing-box",
        License = "GPL-3.0-or-later",
        Protocols = SingBoxConfigBuilder.Supported,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private readonly SingBoxConfigBuilder _builder = new();
    private CancellationTokenSource? _trafficCts;
    private int _apiPort;
    private string _apiSecret = "";

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => SingBoxConfigBuilder.Supported.Contains(profile.Protocol);

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.NotImplemented("sing-box on this platform is planned (VpnService integration)."));
        }

        var exe = locator.Locate(EngineKind.SingBox, settings);
        if (exe is null)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NotInstalled, "sing-box is not installed. Install it from Tools → Engines."));
        }

        if (settings.Mode == ConnectionMode.Tun && !privileges.IsElevated)
        {
            return Task.FromResult(new EngineAvailability(AvailabilityStatus.NeedsElevation, "TUN mode needs administrator rights. Restart BYDLO as administrator or use Local proxy mode.", exe));
        }

        return Task.FromResult(EngineAvailability.Ready(exe));
    }

    protected override async Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var profile = request.Profile ?? throw new EngineException("No profile selected.");
        _apiPort = FindFreeLoopbackPort();
        _apiSecret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Redactor.RegisterSecret(_apiSecret);
        var json = _builder.BuildJson(profile, request.Secrets, new BuildContext(request.Settings, _apiPort, _apiSecret));
        var configPath = Path.Combine(request.WorkDirectory, "sing-box.json");
        await SecureFile.WriteAsync(configPath, json, ct).ConfigureAwait(false);

        return new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = ["run", "-c", configPath, "-D", request.WorkDirectory, "--disable-color"],
            WorkingDirectory = request.WorkDirectory,
            DisplayName = "sing-box",
        };
    }

    protected override bool IsReadyLine(string line) => line.Contains("sing-box started", StringComparison.OrdinalIgnoreCase);

    protected override string? TryParseFatal(string line) =>
        line.Contains("FATAL", StringComparison.Ordinal) || line.Contains("start service:", StringComparison.OrdinalIgnoreCase) ? line : null;

    protected override Task OnStartedAsync(EngineStartRequest request, CancellationToken ct)
    {
        _trafficCts = new CancellationTokenSource();
        _ = ReadTrafficAsync(_trafficCts.Token);
        return Task.CompletedTask;
    }

    protected override Task OnStoppingAsync()
    {
        _trafficCts?.Cancel();
        _trafficCts = null;
        return Task.CompletedTask;
    }

    /// <summary>Reads the Clash API <c>/traffic</c> stream (one JSON line per second) from the local controller.</summary>
    private async Task ReadTrafficAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        long totalUp = 0, totalDown = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_apiPort}/traffic");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiSecret);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                while (!ct.IsCancellationRequested && await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var up = doc.RootElement.GetProperty("up").GetInt64();
                    var down = doc.RootElement.GetProperty("down").GetInt64();
                    totalUp += up;
                    totalDown += down;
                    EmitTraffic(new TrafficSample(DateTimeOffset.Now, up, down, totalUp, totalDown));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or KeyNotFoundException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
    }

    internal static int FindFreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}

/// <summary>Writes files that contain credentials with owner-only permissions where the OS supports it.</summary>
internal static class SecureFile
{
    public static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (OperatingSystem.IsWindows())
        {
            await File.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
            return;
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };
        await using var fs = new FileStream(path, options);
        await using var writer = new StreamWriter(fs);
        await writer.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
    }

    public static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
