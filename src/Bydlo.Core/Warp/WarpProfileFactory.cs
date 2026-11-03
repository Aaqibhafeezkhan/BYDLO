using System.Text;
using Bydlo.Core.Import.Parsers;
using Bydlo.Core.Models;

namespace Bydlo.Core.Warp;

/// <summary>
/// Turns a <see cref="WarpRegistration"/> into a regular WireGuard profile (issue #85).
/// The profile connects through sing-box like any imported config and can be a chain hop.
/// Account id, token and license live in <see cref="Security.ISecretStore"/> with the profile.
/// </summary>
public static class WarpProfileFactory
{
    public const string DefaultEndpoint = "engage.cloudflareclient.com";

    public const int DefaultPort = 2408;

    public static ImportedProfile Create(string name, WarpRegistration reg)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("WARP profile name must not be empty.", nameof(name));
        }

        ValidateKey(reg.PrivateKey, nameof(reg.PrivateKey));
        ValidateKey(reg.PublicKey, nameof(reg.PublicKey));
        ValidateKey(reg.PeerPublicKey, nameof(reg.PeerPublicKey));
        if (reg.EndpointPort is < 1 or > 65535)
        {
            throw new ArgumentException("WARP endpoint port is out of range.", nameof(reg));
        }

        var conf = new StringBuilder();
        conf.AppendLine("[Interface]");
        conf.AppendLine($"PrivateKey = {reg.PrivateKey}");
        conf.Append($"Address = {reg.AddressV4}/32");
        if (!string.IsNullOrWhiteSpace(reg.AddressV6))
        {
            conf.Append($", {reg.AddressV6}/128");
        }

        conf.AppendLine();
        conf.AppendLine("DNS = 1.1.1.1");
        conf.AppendLine("MTU = 1280");
        conf.AppendLine();
        conf.AppendLine("[Peer]");
        conf.AppendLine($"PublicKey = {reg.PeerPublicKey}");
        conf.AppendLine("AllowedIPs = 0.0.0.0/0, ::/0");
        conf.AppendLine($"Endpoint = {reg.EndpointHost}:{reg.EndpointPort}");

        var imported = new WireGuardConfParser().Parse(conf.ToString());
        imported.Profile.Name = name.Trim();
        if (reg.Reserved is { Length: 3 })
        {
            imported.Profile.Options[Keys.Reserved] = $"{reg.Reserved[0]},{reg.Reserved[1]},{reg.Reserved[2]}";
        }

        imported.Secrets.Values[Keys.WarpAccountId] = reg.AccountId;
        imported.Secrets.Values[Keys.WarpToken] = reg.Token;
        if (!string.IsNullOrEmpty(reg.LicenseKey))
        {
            imported.Secrets.Values[Keys.WarpLicense] = reg.LicenseKey;
        }

        return imported;
    }

    private static void ValidateKey(string base64, string param)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("WARP key is not valid base64.", param, ex);
        }

        if (raw.Length != 32)
        {
            throw new ArgumentException("WARP key must decode to 32 bytes.", param);
        }
    }
}
