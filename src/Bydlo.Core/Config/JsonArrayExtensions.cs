using System.Text.Json.Nodes;

namespace Bydlo.Core.Config;

internal static class JsonArrayExtensions
{
    /// <summary>
    /// Adds an already-built node. <see cref="JsonArray.Add{T}(T)"/> binds first for any value and is not trim-safe;
    /// primitives convert to <see cref="JsonNode"/> implicitly, so the generated JSON is unchanged.
    /// </summary>
    public static void AddNode(this JsonArray array, JsonNode? item) => array.Add(item);
}
