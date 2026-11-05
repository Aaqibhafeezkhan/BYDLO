using System.Text.Json.Serialization;

namespace Bydlo.Helper;

/// <summary>Source-generated metadata for the helper's bundle manifest (required for Native AOT).</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class HelperJsonContext : JsonSerializerContext;
