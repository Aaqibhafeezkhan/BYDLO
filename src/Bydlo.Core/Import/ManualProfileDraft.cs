using Bydlo.Core.Models;

namespace Bydlo.Core.Import;

/// <summary>Shared editor model for a basic server. Advanced transports can be imported as links.</summary>
public sealed class ManualProfileDraft
{
    public ProtocolKind Protocol { get; set; } = ProtocolKind.Vless;
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    public string Identifier { get; set; } = "";
    public string Password { get; set; } = "";
    public string Method { get; set; } = "aes-256-gcm";
    public string Sni { get; set; } = "";
    public string Configuration { get; set; } = "";

    public ImportedProfile Build()
    {
        if (!Enum.IsDefined(Protocol)) { throw new FormatException("Invalid protocol."); }
        if (Protocol is ProtocolKind.WireGuard or ProtocolKind.AmneziaWG or ProtocolKind.OpenVpn)
        {
            var result = new ProfileImporter().ImportText(Configuration);
            if (result.Profiles.Count != 1 || result.Profiles[0].Profile.Protocol != Protocol)
            { throw new FormatException("Configuration does not match the selected protocol."); }
            var imported = result.Profiles[0];
            if (!string.IsNullOrWhiteSpace(Name)) { imported.Profile.Name = Name.Trim(); }
            return imported;
        }
        var host = Host.Trim().Trim('[', ']');
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || Port is < 1 or > 65535)
        { throw new FormatException("Invalid server address or port."); }
        if (Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic && !Guid.TryParse(Identifier, out _))
        { throw new FormatException("Invalid identifier."); }
        if (Protocol is ProtocolKind.Trojan or ProtocolKind.Shadowsocks or ProtocolKind.Hysteria2 or ProtocolKind.Tuic && string.IsNullOrWhiteSpace(Password))
        { throw new FormatException("Missing password."); }
        if (Protocol == ProtocolKind.Shadowsocks && string.IsNullOrWhiteSpace(Method))
        { throw new FormatException("Missing cipher."); }
        var profile = new Profile { Protocol = Protocol, Host = host, Port = Port, Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim() };
        var secrets = new ProfileSecrets();
        if (Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Tuic)
        { secrets.Values[Keys.Uuid] = Identifier.Trim(); }
        else if (Protocol is ProtocolKind.Socks5 or ProtocolKind.Http && !string.IsNullOrWhiteSpace(Identifier))
        { profile.Options[Keys.Username] = Identifier; }
        if (Password.Length > 0) { secrets.Values[Keys.Password] = Password; }
        if (Protocol == ProtocolKind.Shadowsocks) { profile.Options[Keys.Method] = Method.Trim(); }
        if (Protocol == ProtocolKind.Vmess) { profile.Options[Keys.Method] = "auto"; }
        if (Protocol is ProtocolKind.Vless or ProtocolKind.Vmess or ProtocolKind.Trojan or ProtocolKind.Hysteria2 or ProtocolKind.Tuic)
        {
            profile.Options[Keys.Security] = "tls";
            if (!string.IsNullOrWhiteSpace(Sni)) { profile.Options[Keys.Sni] = Sni.Trim(); }
        }
        return new(profile, secrets);
    }
}
