using System.Text.Json;
using System.Text.Json.Nodes;
using Bydlo.Core.Models;

namespace Bydlo.Core.Import.Parsers;

/// <summary>Validates the XHTTP <c>extra</c> link parameter (URI-decoded JSON, §4.3.19).
/// Only an allowlist of SplitHTTP fields is accepted, like <see cref="CustomJsonConfig"/>.
/// Never logs or echoes the raw value; throws <see cref="ImportException"/> on misuse.</summary>
internal static class XhttpExtraValidator
{
    private static readonly HashSet<string> TopLevel = new(StringComparer.Ordinal)
    {
        "headers", "xPaddingBytes", "xPaddingObfsMode", "xPaddingKey", "xPaddingHeader",
        "xPaddingPlacement", "xPaddingMethod", "uplinkHTTPMethod",
        "sessionIDPlacement", "sessionIDKey", "sessionIDTable", "sessionIDLength",
        "seqPlacement", "seqKey", "uplinkDataPlacement", "uplinkDataKey", "uplinkChunkSize",
        "noGRPCHeader", "noSSEHeader",
        "scMaxEachPostBytes", "scMinPostsIntervalMs", "scMaxBufferedPosts", "scStreamUpServerSecs",
        "serverMaxHeaderBytes", "xmux",
    };

    private static readonly HashSet<string> XmuxKeys = new(StringComparer.Ordinal)
    {
        "maxConcurrency", "maxConnections", "cMaxReuseTimes",
        "hMaxRequestTimes", "hMaxReusableSecs", "hKeepAlivePeriod",
    };

    private const int MaxLength = 8192;

    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ImportException("The XHTTP extra parameter is empty.");
        }
        if (raw.Length > MaxLength)
        {
            throw new ImportException("The XHTTP extra parameter is too large.");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw new ImportException("The XHTTP extra parameter is not valid JSON.");
        }

        if (node is not JsonObject obj)
        {
            throw new ImportException("The XHTTP extra parameter must be a JSON object.");
        }

        foreach (var (key, value) in obj)
        {
            if (!TopLevel.Contains(key))
            {
                throw new ImportException("The XHTTP extra parameter contains an unsupported field.");
            }
            ValidateValue(value, key);
        }

        if (obj.TryGetPropertyValue("xmux", out var xmux) && xmux is JsonObject xmuxObj)
        {
            foreach (var (key, value) in xmuxObj)
            {
                if (!XmuxKeys.Contains(key))
                {
                    throw new ImportException("The XHTTP extra parameter contains an unsupported field.");
                }
                ValidateValue(value, "xmux." + key);
            }
        }

        if (obj.TryGetPropertyValue("headers", out var headers) && headers is JsonObject headersObj)
        {
            foreach (var (key, value) in headersObj)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
                {
                    throw new ImportException("The XHTTP extra parameter contains an invalid header.");
                }
                if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text) || text.Length > 2048)
                {
                    throw new ImportException("The XHTTP extra parameter contains an invalid header.");
                }
                RejectExternal(text);
            }
        }

        return obj.ToJsonString();
    }

    private static void ValidateValue(JsonNode? value, string field)
    {
        switch (value)
        {
            case null:
                throw new ImportException("The XHTTP extra parameter contains an invalid value.");
            case JsonObject o when field.StartsWith("xmux.", StringComparison.Ordinal):
                // xmux range objects like {"from":..,"to":..} are validated structurally below.
                foreach (var (_, child) in o)
                {
                    ValidateValue(child, field);
                }
                break;
            case JsonObject when field is "headers" or "xmux":
                // Contents are validated by the dedicated branches above.
                break;
            case JsonObject:
                throw new ImportException("The XHTTP extra parameter contains an invalid value.");
            case JsonArray array:
                if (array.Count > 32)
                {
                    throw new ImportException("The XHTTP extra parameter contains an invalid value.");
                }
                foreach (var child in array)
                {
                    ValidateValue(child, field);
                }
                break;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text):
                if (text.Length > 2048)
                {
                    throw new ImportException("The XHTTP extra parameter contains an invalid value.");
                }
                RejectExternal(text);
                break;
            case JsonValue:
                // numbers and booleans are fine
                break;
        }
    }

    private static void RejectExternal(string text)
    {
        if (text.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("ext-ip:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportException("The XHTTP extra parameter contains an unsupported value.");
        }
    }
}
