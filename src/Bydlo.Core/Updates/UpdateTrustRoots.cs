namespace Bydlo.Core.Updates;

/// <summary>
/// Compiled-in trust root for the signed update catalog (issue #411). The public key is never read
/// from the feed: only these constants decide which catalog signature is accepted, so replacing both
/// the package and the metadata still fails unless the catalog was signed by a key listed here.
/// </summary>
public static class UpdateTrustRoots
{
    /// <summary>Key that signs the catalog published today.</summary>
    public const string CurrentKeyId = "bydlo-update-2026-01";

    /// <summary>
    /// ECDSA P-256 public key (SPKI, base64). Its private half exists only as the
    /// <c>BYDLO_UPDATE_SIGNING_KEY</c> repository secret.
    /// </summary>
    public const string CurrentPublicKeySpki =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEiKdAbKzr0xwsC1aT75Myp51OP14rTcaprgApMzEwFa7u6qCXmgkIpxQYyH0Xp43C+ayYicp0OiSVi8yYh9/+9Q==";

    /// <summary>Rotation slot: publish the next key here before the catalog switches to it.</summary>
    public const string NextKeyId = "bydlo-update-2026-02";

    /// <summary>Empty until the maintainer generates the next key pair.</summary>
    public const string NextPublicKeySpki = "";

    /// <summary>Key ids that must never verify again, even when a catalog still names them.</summary>
    public static IReadOnlyList<string> RevokedKeyIds { get; } = [];

    /// <summary>keyId → SPKI base64. Unset slots are omitted, so an unused rotation slot is harmless.</summary>
    public static IReadOnlyDictionary<string, string> Keys { get; } = BuildKeys();

    private static Dictionary<string, string> BuildKeys()
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        Add(keys, CurrentKeyId, CurrentPublicKeySpki);
        Add(keys, NextKeyId, NextPublicKeySpki);
        return keys;
    }

    private static void Add(Dictionary<string, string> keys, string keyId, string spki)
    {
        // A placeholder is not a key: an unset slot must refuse every catalog instead of trusting garbage.
        if (string.IsNullOrWhiteSpace(spki) || spki.StartsWith("PLACEHOLDER", StringComparison.Ordinal))
        {
            return;
        }

        keys[keyId] = spki;
    }
}
