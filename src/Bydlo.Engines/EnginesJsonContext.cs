using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bydlo.Engines.Installation;
using Bydlo.Engines.SingBox;

namespace Bydlo.Engines;

/// <summary>Source-generated metadata for engine files (default serializer settings).</summary>
[JsonSerializable(typeof(EngineLock))]
[JsonSerializable(typeof(AmneziaHostConfig))]
internal sealed partial class EnginesJsonContext : JsonSerializerContext;

/// <summary>Clash API payloads, read with the web defaults that <c>System.Net.Http.Json</c> used before.</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ClashConnectionsClient.ConnectionList))]
internal sealed partial class ClashJsonContext : JsonSerializerContext;

/// <summary>Configuration read by the bundled AmneziaWG host; the property names are its wire format.</summary>
internal sealed record AmneziaHostConfig(string Name, int MTU, string UAPI);
