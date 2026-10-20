using System.Text.Json.Serialization;

namespace PuddingBrowser.Protocol;

[JsonSerializable(typeof(BrowserBridgeEnvelope))]
[JsonSerializable(typeof(BrowserBridgeHello))]
[JsonSerializable(typeof(BrowserBridgeHelloAck))]
[JsonSerializable(typeof(BrowserBridgeBuildIdentity))]
[JsonSerializable(typeof(BrowserBridgeCommand))]
[JsonSerializable(typeof(BrowserBridgeCommandResult))]
[JsonSerializable(typeof(BrowserBridgeCancel))]
[JsonSerializable(typeof(BrowserBridgeEvent))]
// Command payload DTOs
[JsonSerializable(typeof(ContextCreateArguments))]
[JsonSerializable(typeof(ContextCloseArguments))]
[JsonSerializable(typeof(ContextGetInfoArguments))]
[JsonSerializable(typeof(PageCreateArguments))]
[JsonSerializable(typeof(PageGotoArguments))]
[JsonSerializable(typeof(PageActivateArguments))]
[JsonSerializable(typeof(PageCloseArguments))]
[JsonSerializable(typeof(BrowserLocatorDescriptor))]
[JsonSerializable(typeof(PageSnapshotArguments))]
[JsonSerializable(typeof(PageLocateArguments))]
[JsonSerializable(typeof(PageInteractArguments))]
[JsonSerializable(typeof(PageWaitForArguments))]
// 宿主侧文档预览：只传路径，渲染由 Desktop 自有组件负责
[JsonSerializable(typeof(DocumentPreviewArguments))]
// Result descriptor DTOs
[JsonSerializable(typeof(BrowserContextDescriptor))]
[JsonSerializable(typeof(BrowserPageDescriptor))]
[JsonSerializable(typeof(BrowserNavigationResultDescriptor))]
[JsonSerializable(typeof(BrowserPageListDescriptor))]
[JsonSerializable(typeof(BrowserContextListDescriptor))]
[JsonSerializable(typeof(BrowserSnapshotDescriptor))]
[JsonSerializable(typeof(BrowserBoundingBoxDescriptor))]
[JsonSerializable(typeof(BrowserElementDescriptor))]
[JsonSerializable(typeof(BrowserLocateResultDescriptor))]
[JsonSerializable(typeof(BrowserInteractionResultDescriptor))]
[JsonSerializable(typeof(BrowserScrollReportDescriptor))]
[JsonSerializable(typeof(BrowserWaitResultDescriptor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class BrowserBridgeJsonSerializerContext : JsonSerializerContext
{
}
