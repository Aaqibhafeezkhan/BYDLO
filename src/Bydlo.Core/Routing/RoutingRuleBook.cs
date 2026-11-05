using System.Text.Json;
using System.Text.Json.Serialization;
using Bydlo.Core.Security;

namespace Bydlo.Core.Routing;

/// <summary>Portable rules and pinned sources. Import never downloads anything.</summary>
public sealed record RoutingRuleBook(List<OrderedRoutingRule> Rules, List<RuleSetSource> Sources)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<RoutingCondition>(), new JsonStringEnumConverter<RoutingAction>() },
        TypeInfoResolver = Serialization.CoreJsonContext.Default,
    };

    public void Validate()
    {
        if (Rules is null || Sources is null || Rules.Count > 10000 || Sources.Count > 256)
        {
            throw new InvalidDataException("routing.rules.invalid");
        }
        foreach (var rule in Rules) { OrderedRoutingRules.Validate(rule); }
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            ValidateSource(source);
            if (!tags.Add(source.Tag) || RoutingPresets.Find(source.Tag) is not null)
            {
                throw new InvalidDataException("routing.sources.invalid");
            }
        }
    }

    public static void ValidateSource(RuleSetSource source)
    {
        if (source is null || string.IsNullOrEmpty(source.Tag) || source.Tag.Length > 100 ||
            !source.Tag.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.') ||
            source.Url is null || !source.Url.IsAbsoluteUri || source.Url.Scheme != Uri.UriSchemeHttps ||
            source.Url.UserInfo.Length != 0 || source.Url.Fragment.Length != 0 ||
            source.MaxBytes is <= 0 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("routing.sources.invalid");
        }
        try { IntegrityVerifier.NormalizeDigest(source.Sha256); }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidDataException("routing.sources.invalid", ex);
        }
    }

    public string Export()
    {
        Validate();
        return JsonSerializer.Serialize(this, Serialization.BydloJson.Info<RoutingRuleBook>(Json));
    }

    public static RoutingRuleBook Import(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 1024 * 1024) { throw new InvalidDataException("routing.rules.invalid"); }
        using var document = JsonDocument.Parse(bytes.ToArray());
        // Preserve compatibility with the original rules-only array format.
        var book = document.RootElement.ValueKind == JsonValueKind.Array
            ? new RoutingRuleBook(document.RootElement.Deserialize(Serialization.BydloJson.Info<List<OrderedRoutingRule>>(Json))!, [])
            : document.RootElement.Deserialize(Serialization.BydloJson.Info<RoutingRuleBook>(Json))
                ?? throw new InvalidDataException("routing.rules.invalid");
        book.Validate();
        return book;
    }
}
