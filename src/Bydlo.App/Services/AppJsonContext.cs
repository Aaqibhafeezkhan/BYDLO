using System.Text.Json.Serialization;
using Bydlo.Core.Diagnostics;
using Bydlo.Core.Models;

namespace Bydlo.App.Services;

/// <summary>Source-generated metadata for UI-side JSON (default serializer settings).</summary>
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(ZapretSettings))]
[JsonSerializable(typeof(ZapretSelectionReport))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>Indented files written for the user.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ZapretSelectionReport))]
[JsonSerializable(typeof(ThemeTemplate))]
internal sealed partial class AppIndentedJsonContext : JsonSerializerContext;

/// <summary>Exported custom theme template; the lower-case names are the theme file format.</summary>
#pragma warning disable IDE1006 // Property names are the JSON wire format.
internal sealed record ThemeTemplate(string id, string name, string author, int version, string @base, Dictionary<string, string> colors);
#pragma warning restore IDE1006
