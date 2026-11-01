using System.Security.Cryptography;
using System.Text;
using Bydlo.Core.Connection;
using Bydlo.Core.Engines;
using Bydlo.Core.Models;
using Bydlo.Core.Processes;
using Bydlo.Core.Security;
using Bydlo.Engines.Installation;

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
/// Flowseal tg-ws-proxy in headless mode (the <c>tg-ws-proxy</c> CLI entry point): a local MTProto proxy that tunnels
/// Telegram Desktop traffic over WebSocket. No tray GUI is started.
/// </summary>
public sealed class TgWsProxyEngine(IProcessRunner runner, LogRedactor redactor, EngineLocator locator, TgWsProxyRuntime runtime)
    : ProcessEngineBase(runner, redactor)
{
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
            [OsFamily.Android] = SupportLevel.Unsupported,
        },
    };

    public override EngineDescriptor Descriptor => Info;

    public override bool CanHandle(Profile profile) => false;

    public override Task<EngineAvailability> CheckAvailabilityAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (Info.SupportOn(locator.Platform.Os) != SupportLevel.Implemented)
        {
            return Task.FromResult(EngineAvailability.Unsupported("TgWsProxy needs a Python runtime, which BYDLO does not provide on this platform.", "error.engine.tg.platform"));
        }

        var custom = settings.TgWsProxy.CustomExecutable;
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return Task.FromResult(File.Exists(custom)
                ? EngineAvailability.Ready(custom)
                : new EngineAvailability(AvailabilityStatus.NotInstalled, "The configured tg-ws-proxy executable was not found.") { LocalizationKey = "error.engine.tg.custom" });
        }

        var managed = runtime.Executable;
        return Task.FromResult(managed is not null
            ? EngineAvailability.Ready(managed)
            : new EngineAvailability(AvailabilityStatus.NotInstalled, "TgWsProxy runtime is not installed. Install it in Tools → TgWsProxy (needs Python 3.9+).") { LocalizationKey = "error.engine.tg.missing" });
    }

    public static IReadOnlyList<string> BuildArguments(TgWsProxySettings s, string secret, string? logFile)
    {
        var args = new List<string> { "--host", s.Host, "--port", s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--secret", secret };
        if (s.DcIps.Count == 0)
        {
            // An empty --dc-ip clears upstream defaults (argparse nargs='?').
            args.Add("--dc-ip");
        }

        foreach (var dc in s.DcIps)
        {
            args.AddRange(["--dc-ip", dc]);
        }

        if (!string.IsNullOrWhiteSpace(s.FakeTlsDomain))
        {
            args.AddRange(["--fake-tls-domain", s.FakeTlsDomain.Trim()]);
        }

        if (!s.CfProxyFallback)
        {
            args.Add("--no-cfproxy");
        }

        if (s.Verbose)
        {
            args.Add("-v");
        }

        if (logFile is not null)
        {
            args.AddRange(["--log-file", logFile]);
        }

        return args;
    }

    protected override Task<ProcessSpec> PrepareAsync(EngineStartRequest request, EngineAvailability availability, CancellationToken ct)
    {
        var secret = request.Secrets["secret"];
        if (!TgLinkBuilder.IsValidSecret(secret))
        {
            throw new EngineException("TgWsProxy secret is missing or invalid. Regenerate it in Tools → TgWsProxy.");
        }

        return Task.FromResult(new ProcessSpec
        {
            FileName = availability.ExecutablePath!,
            Arguments = BuildArguments(request.Settings.TgWsProxy, secret!, null),
            WorkingDirectory = request.WorkDirectory,
            DisplayName = "tg-ws-proxy",
            Environment = new Dictionary<string, string> { ["PYTHONUNBUFFERED"] = "1", ["PYTHONIOENCODING"] = "utf-8" },
        });
    }

    protected override bool IsReadyLine(string line) => line.Contains("Listening on", StringComparison.Ordinal);

    protected override string? TryParseFatal(string line) =>
        line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase) || line.Contains("WinError 10048", StringComparison.Ordinal)
            ? "The TgWsProxy port is already in use."
            : line.Contains("ERROR", StringComparison.Ordinal) && line.Contains("Secret", StringComparison.Ordinal) ? line : null;
}

/// <summary>
/// Private Python virtualenv with the pinned tg-ws-proxy package (headless CLI). Created only on user request,
/// inside BYDLO's engines folder.
/// </summary>
public sealed class TgWsProxyRuntime(string enginesDirectory, EngineLock engineLock, IProcessRunner runner)
{
    public string VenvDirectory => Path.Combine(enginesDirectory, "tg-ws-proxy", "venv");

    public string? Executable
    {
        get
        {
            var exe = OperatingSystem.IsWindows()
                ? Path.Combine(VenvDirectory, "Scripts", "tg-ws-proxy.exe")
                : Path.Combine(VenvDirectory, "bin", "tg-ws-proxy");
            return File.Exists(exe) ? exe : null;
        }
    }

    public async Task InstallAsync(string pythonExecutable, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var pip = engineLock.Find("tg-ws-proxy")?.Pip ?? throw new InvalidOperationException("tg-ws-proxy is not pinned in engines.lock.json.");
        Directory.CreateDirectory(Path.GetDirectoryName(VenvDirectory)!);

        progress?.Report("python -m venv");
        await RunAsync(pythonExecutable, ["-m", "venv", VenvDirectory], ct).ConfigureAwait(false);

        var venvPython = OperatingSystem.IsWindows()
            ? Path.Combine(VenvDirectory, "Scripts", "python.exe")
            : Path.Combine(VenvDirectory, "bin", "python");
        progress?.Report("pip install " + pip);
        await RunAsync(venvPython, ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", pip], ct).ConfigureAwait(false);
        if (Executable is null)
        {
            throw new EngineException("tg-ws-proxy was installed but its entry point was not found.");
        }
    }

    private async Task RunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        // Package installation does not touch system networking, but it is still gated in development.
        var (code, output) = await runner.RunToCompletionAsync(
            new ProcessSpec { FileName = exe, Arguments = args, DisplayName = "python", AffectsNetwork = true },
            TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
        if (code != 0)
        {
            var tail = string.Join('\n', output.Split('\n').TakeLast(6));
            throw new EngineException($"Installing the TgWsProxy runtime failed (code {code}):\n{tail}");
        }
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
