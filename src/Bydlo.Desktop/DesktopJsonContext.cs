using System.Text.Json.Serialization;

namespace Bydlo.Desktop;

/// <summary>Source-generated metadata for single-instance activation messages.</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class DesktopJsonContext : JsonSerializerContext;
