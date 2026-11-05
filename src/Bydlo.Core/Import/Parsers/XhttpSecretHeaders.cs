using System.Text.Json.Nodes;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import.Parsers;

/// <summary>
/// Keeps XHTTP transport credentials out of plain profile documents (issue #338).
/// The <c>headers</c> object of the normalized <c>extra</c> parameter may carry a
/// bearer token or cookie, so it lives in <see cref="Keys.XhttpHeaders"/> inside
/// <see cref="ProfileSecrets"/> while the remaining tuning fields stay in
/// <see cref="Keys.XhttpExtra"/> options. Assembly back together happens only at
/// config generation or explicit export.
/// </summary>
internal static class XhttpSecretHeaders
{
    /// <summary>
    /// Tries to split the <c>headers</c> object out of a normalized extra JSON value.
    /// Returns false when there is nothing secret (or the value is not an object).
    /// </summary>
    public static bool TryExtractHeaders(string? extraRaw, out string? publicExtra, out string? headersJson)
    {
        publicExtra = null;
        headersJson = null;
        if (string.IsNullOrWhiteSpace(extraRaw))
        {
            return false;
        }
        JsonObject obj;
        try
        {
            if (JsonNode.Parse(extraRaw) is not JsonObject parsed)
            {
                return false;
            }
            obj = parsed;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        if (obj.TryGetPropertyValue("headers", out var headers) && headers is JsonObject headersObj)
        {
            headersJson = headersObj.ToJsonString();
            obj.Remove("headers");
            publicExtra = obj.Count == 0 ? null : obj.ToJsonString();
            return true;
        }
        return false;
    }

    /// <summary>Moves headers from profile options into secrets. Returns true when anything moved.</summary>
    public static bool Split(Profile profile, ProfileSecrets secrets)
    {
        if (!profile.Options.TryGetValue(Keys.XhttpExtra, out var raw))
        {
            return false;
        }
        if (!TryExtractHeaders(raw, out var publicExtra, out var headersJson) || headersJson is null)
        {
            return false;
        }
        secrets.Values[Keys.XhttpHeaders] = headersJson;
        if (publicExtra is null) { profile.Options.Remove(Keys.XhttpExtra); }
        else { profile.Options[Keys.XhttpExtra] = publicExtra; }
        return true;
    }

    /// <summary>Strips headers from an options snapshot (e.g. subscription baselines). Returns true when changed.</summary>
    public static bool Strip(IDictionary<string, string> options)
    {
        if (!options.TryGetValue(Keys.XhttpExtra, out var raw))
        {
            return false;
        }
        if (!TryExtractHeaders(raw, out var publicExtra, out _))
        {
            return false;
        }
        if (publicExtra is null) { options.Remove(Keys.XhttpExtra); }
        else { options[Keys.XhttpExtra] = publicExtra; }
        return true;
    }

    /// <summary>Reassembles the full extra JSON from its public part and secret headers. Null when neither exists.</summary>
    public static string? Merge(string? publicExtra, string? secretHeaders)
    {
        if (string.IsNullOrWhiteSpace(publicExtra) && string.IsNullOrWhiteSpace(secretHeaders))
        {
            return null;
        }
        JsonObject obj;
        if (string.IsNullOrWhiteSpace(publicExtra))
        {
            obj = new JsonObject();
        }
        else if (JsonNode.Parse(publicExtra) is JsonObject parsed)
        {
            obj = parsed;
        }
        else
        {
            throw new ImportException("The XHTTP extra parameter is not valid JSON.");
        }
        if (!string.IsNullOrWhiteSpace(secretHeaders))
        {
            if (JsonNode.Parse(secretHeaders) is not JsonObject headersObj)
            {
                throw new ImportException("The XHTTP headers are not valid.");
            }
            obj["headers"] = headersObj;
        }
        return obj.ToJsonString();
    }
}
