using System.Text.Json.Serialization;

namespace PuddingBrowser.Protocol;

[JsonSerializable(typeof(BrowserBridgeEnvelope))]
[JsonSerializable(typeof(BrowserBridgeHello))]
[JsonSerializable(typeof(BrowserBridgeHelloAck))]
[JsonSerializable(typeof(BrowserBridgeCommand))]
[JsonSerializable(typeof(BrowserBridgeCommandResult))]
[JsonSerializable(typeof(BrowserBridgeCancel))]
[JsonSerializable(typeof(BrowserBridgeEvent))]
// Command payload DTOs
[JsonSerializable(typeof(ContextCreateArguments))]
[JsonSerializable(typeof(ContextCloseArguments))]
[JsonSerializable(typeof(PageCreateArguments))]
[JsonSerializable(typeof(PageGotoArguments))]
[JsonSerializable(typeof(PageActivateArguments))]
[JsonSerializable(typeof(PageCloseArguments))]
// Result descriptor DTOs
[JsonSerializable(typeof(BrowserContextDescriptor))]
[JsonSerializable(typeof(BrowserPageDescriptor))]
[JsonSerializable(typeof(BrowserNavigationResultDescriptor))]
[JsonSerializable(typeof(BrowserPageListDescriptor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
internal sealed partial class BrowserBridgeJsonSerializerContext : JsonSerializerContext
{
}
