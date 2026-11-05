using System.Text.Json.Serialization;
using Bydlo.Core.Platform;
using Bydlo.Platform.Processes;

namespace Bydlo.Platform;

/// <summary>Source-generated metadata for Platform journals and stores (default serializer settings).</summary>
[JsonSerializable(typeof(ZapretFirewallLease))]
[JsonSerializable(typeof(AmneziaNetworkLease))]
[JsonSerializable(typeof(MacAmneziaNetwork.Lease), TypeInfoPropertyName = "MacAmneziaLease")]
[JsonSerializable(typeof(JournaledSystemProxyService.ProxyJournal), TypeInfoPropertyName = "DesktopProxyJournal")]
[JsonSerializable(typeof(JournaledSystemProxyService.MacProxy))]
[JsonSerializable(typeof(WindowsProxyRecovery.ProxyJournal), TypeInfoPropertyName = "WindowsProxyJournal")]
[JsonSerializable(typeof(List<JournalEntry>))]
[JsonSerializable(typeof(HelperBuild))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class PlatformJsonContext : JsonSerializerContext;
