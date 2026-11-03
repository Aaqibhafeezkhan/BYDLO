using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

/// <summary>Validates editor input and returns independent snapshots, leaving the saved profile untouched.</summary>
public sealed class ProfileEditDraft(Profile source, ProfileSecrets secrets)
{
    private readonly bool _usesConfiguration = secrets[Keys.RawConfig] is not null;
    public string Name { get; set; } = source.Name;
    public string Host { get; set; } = source.Host;
    public int Port { get; set; } = source.Port;
    public EngineKind? PreferredEngine { get; set; } = source.PreferredEngine;
    public Dictionary<string, string> Options { get; } = new(source.Options, StringComparer.OrdinalIgnoreCase);
    public ProfileSecrets Secrets { get; } = new() { Values = new(secrets.Values, StringComparer.OrdinalIgnoreCase) };
    public string? Configuration { get; set; } = secrets[Keys.RawConfig];

    public ImportedProfile Build()
    {
        if (string.IsNullOrWhiteSpace(Name)) { throw new FormatException("Missing profile name."); }
        if (_usesConfiguration && string.IsNullOrWhiteSpace(Configuration))
        { throw new FormatException("Missing profile configuration."); }
        var host = Host.Trim().Trim('[', ']');
        var port = Port;
        var options = new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(Secrets.Values, StringComparer.OrdinalIgnoreCase);
        if (Configuration is not null)
        {
            var parsed = new ProfileImporter().ImportText(Configuration);
            if (parsed.Profiles.Count != 1 || parsed.Profiles[0].Profile.Protocol != source.Protocol || parsed.Errors.Count != 0)
            { throw new FormatException("Configuration does not match the profile protocol."); }
            var imported = parsed.Profiles[0];
            host = imported.Profile.Host;
            port = imported.Profile.Port;
            options = new(imported.Profile.Options, StringComparer.OrdinalIgnoreCase);
            values.Remove("privateKey");
            values.Remove("presharedKey");
            foreach (var (key, value) in imported.Secrets.Values) { values[key] = value; }
        }
        else
        {
            if (source.Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic &&
                !Guid.TryParse(values.GetValueOrDefault(Keys.Uuid), out _) ||
                source.Protocol is ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic &&
                string.IsNullOrWhiteSpace(values.GetValueOrDefault(Keys.Password)))
            { throw new FormatException("Invalid profile credentials."); }
            if (options.GetValueOrDefault(Keys.Security) is { Length: > 0 } security && security is not ("none" or "tls" or "reality") ||
                options.GetValueOrDefault(Keys.Transport) is { Length: > 0 } transport && transport is not ("tcp" or "raw" or "ws" or "grpc" or "http" or "h2" or "httpupgrade" or "xhttp" or "splithttp" or "quic") ||
                options.GetValueOrDefault(Keys.Sni) is { Length: > 0 } sni && Uri.CheckHostName(sni) == UriHostNameType.Unknown)
            { throw new FormatException("Invalid protocol options."); }
        }
        if (source.Protocol is not (ProtocolKind.SingBoxConfig or ProtocolKind.XrayConfig) &&
            (Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535))
        { throw new FormatException("Invalid server address or port."); }
        if (source.Protocol == ProtocolKind.OpenVpn && options.GetValueOrDefault("authUserPass") == "true")
        { Security.OpenVpnCredentials.Validate(values.GetValueOrDefault(Keys.Username), values.GetValueOrDefault(Keys.Password)); }
        return new(new Profile
        {
            Id = source.Id, Protocol = source.Protocol, Name = Name.Trim(), Host = host, Port = port,
            Options = options, PreferredEngine = PreferredEngine, SubscriptionId = source.SubscriptionId,
            AddedAt = source.AddedAt, IsFavorite = source.IsFavorite,
        }, new ProfileSecrets { Values = values });
    }
}
