using System.Security.Cryptography;
using System.Text;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Security;

namespace Bydlo.Engines.TgWsProxy;

/// <summary>Builds <c>tg://proxy</c> links exactly like Flowseal's tray app.</summary>
public static class TgLinkBuilder
{
    public static string NewSecret() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidSecret(string? secret) =>
        secret is { Length: 32 } && secret.All(Uri.IsHexDigit);

    /// <summary>
    /// <c>dd</c>+secret for the padded-intermediate mode; with Fake TLS: <c>ee</c>+secret+hex(domain).
    /// Wildcard listen addresses are replaced by 127.0.0.1 for the link.
    /// </summary>
    public static string Build(string host, int port, string secret, string? fakeTlsDomain = null)
    {
        var linkHost = host is "0.0.0.0" or "::" or "" ? "127.0.0.1" : host;
        var domain = fakeTlsDomain?.Trim();
        var s = string.IsNullOrEmpty(domain)
            ? "dd" + secret
            : "ee" + secret + Convert.ToHexStringLower(Encoding.ASCII.GetBytes(domain));
        return $"tg://proxy?server={linkHost}&port={port}&secret={s}";
    }
}

/// <summary>
/// TgWsProxy built into BYDLO: a native C# port of Flowseal tg-ws-proxy running in-process
/// (<see cref="TgWsProxyServer"/>). No Python, no external executable, no window.
/// </summary>
public sealed class TgWsProxyEngine(LogRedactor redactor, ITgNetwork? network = null, TimeProvider? time = null) : IEngine
{
    /// <summary>Upstream release the native port follows.</summary>
    public const string UpstreamVersion = "1.11.4";

    public const string BuiltIn = "built-in";

    public static readonly EngineDescriptor Info = new()
    {
        Kind = EngineKind.TgWsProxy,
        DisplayName = "TgWsProxy (Flowseal)",
        Upstream = "https://github.com/Flowseal/tg-ws-proxy",
        License = "MIT",
        IsTool = true,
        Platforms = new Dictionary<OsFamily, SupportLevel>
        {
            [OsFamily.Windows] = SupportLevel.Implemented,
            [OsFamily.Linux] = SupportLevel.Implemented,
            [OsFamily.MacOS] = SupportLevel.Implemented,
            [OsFamily.Android] = SupportLevel.Planned,
        },
    };

    private readonly Lock _gate = new();
    private TgWsProxyServer? _server;
    private CancellationTokenSource? _traffic;

    public EngineDescriptor Descriptor => Info;

    public EngineState State { get; private set; } = EngineState.Stopped;

    public event EventHandler<EngineStateChangedEventArgs>? StateChanged;

    public event EventHandler<string>? Log;

    public event EventHandler<TrafficSample>? Traffic;

    public bool CanHandle(Profile profile) => false;

    public Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default) =>
        Task.FromResult(Info.SupportOn(PlatformInfo.Current.Os) == SupportLevel.Implemented
            ? EngineAvailability.Ready(BuiltIn)
            : EngineAvailability.NotImplemented("TgWsProxy is not available on this platform yet.", "error.engine.tg.platform"));

    public async Task StartAsync(EngineStartRequest request, CancellationToken ct = default)
    {
        ChainGuards.ThrowIfChained(EngineKind.TgWsProxy, request);
        lock (_gate)
        {
            if (_server is not null || State is EngineState.Running or EngineState.Starting)
            {
                throw new EngineException($"{Info.DisplayName} is already running.");
            }

            SetState(EngineState.Starting);
        }

        TgWsProxyServer? server = null;
        try
        {
            var availability = await CheckAvailabilityAsync(request.Settings, ct).ConfigureAwait(false);
            if (!availability.IsReady)
            {
                throw new EngineException(availability.Message, localizationKey: availability.LocalizationKey);
            }

            var secret = request.Secrets["secret"];
            if (!TgLinkBuilder.IsValidSecret(secret))
            {
                throw new EngineException("TgWsProxy secret is missing or invalid. Regenerate it in Tools → TgWsProxy.", localizationKey: "error.engine.tg.secret");
            }

            redactor.RegisterSecret(secret);
            var options = TgWsProxyOptions.From(request.Settings.TgWsProxy, secret!);

            // The in-process proxy opens real sockets: same development lock as any external engine.
            NetworkSafetyGuard.EnsureAllowed("start TgWsProxy");

            server = new TgWsProxyServer(options, network ?? new SystemTgNetwork(options.BufferSize), OnServerLog, time);
            server.Faulted += (_, message) => _ = FailAsync(message);
            await server.StartAsync(ct).ConfigureAwait(false);

            lock (_gate)
            {
                _server = server;
                _traffic = new CancellationTokenSource();
            }

            _ = ReportTrafficAsync(server, _traffic.Token);
            SetState(EngineState.Running);
        }
        catch (Exception ex)
        {
            if (server is not null)
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }

            var message = ex is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.AddressAlreadyInUse }
                ? "The TgWsProxy port is already in use."
                : ex.Message;
            SetState(EngineState.Failed, message);
            throw ex as EngineException ?? new EngineException(message, ex);
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        SetState(EngineState.Stopping);
        await StopServerAsync().ConfigureAwait(false);
        SetState(EngineState.Stopped);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task FailAsync(string message)
    {
        await StopServerAsync().ConfigureAwait(false);
        SetState(EngineState.Failed, message);
    }

    private async Task StopServerAsync()
    {
        TgWsProxyServer? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
            _traffic?.Cancel();
            _traffic?.Dispose();
            _traffic = null;
        }

        if (server is not null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnServerLog(TgLogLevel level, string message)
    {
        var prefix = level switch
        {
            TgLogLevel.Debug => "DEBUG",
            TgLogLevel.Warning => "WARN ",
            TgLogLevel.Error => "ERROR",
            _ => "INFO ",
        };
        Log?.Invoke(this, redactor.Redact($"{prefix}  {message}"));
    }

    private async Task ReportTrafficAsync(TgWsProxyServer server, CancellationToken ct)
    {
        var clock = time ?? TimeProvider.System;
        long lastUp = 0, lastDown = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), clock, ct).ConfigureAwait(false);
                var up = server.Stats.BytesUp;
                var down = server.Stats.BytesDown;
                Traffic?.Invoke(this, new TrafficSample(clock.GetLocalNow(), up - lastUp, down - lastDown, up, down));
                (lastUp, lastDown) = (up, down);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void SetState(EngineState state, string? error = null)
    {
        State = state;
        StateChanged?.Invoke(this, new EngineStateChangedEventArgs(state, error));
    }
}

/// <summary>Ensures the TgWsProxy secret exists in the secret store and builds the Telegram link.</summary>
public sealed class TgWsProxySecrets(ISecretStore store)
{
    public async Task<string> GetOrCreateAsync(CancellationToken ct = default)
    {
        var secrets = await store.LoadAsync(ConnectionManager.TgWsProxySecretId, ct).ConfigureAwait(false);
        if (TgLinkBuilder.IsValidSecret(secrets["secret"]))
        {
            return secrets["secret"]!;
        }

        return await RegenerateAsync(ct).ConfigureAwait(false);
    }

    public async Task<string> RegenerateAsync(CancellationToken ct = default)
    {
        var secret = TgLinkBuilder.NewSecret();
        await store.SaveAsync(ConnectionManager.TgWsProxySecretId, new ProfileSecrets { Values = { ["secret"] = secret } }, ct).ConfigureAwait(false);
        return secret;
    }
}
