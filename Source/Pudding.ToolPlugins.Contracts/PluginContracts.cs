using System.Text.Json;

namespace Pudding.ToolPlugins.Contracts;

public static class PluginJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
}

public sealed record PluginManifest
{
    public string Schema { get; init; } = "pudding-tool-plugin/v2";
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    public string SdkApi { get; init; } = "1.0";
    public string CoreApi { get; init; } = "1.0";
    public PluginTarget Runtime { get; init; } = new();
    public required PluginEntry Entry { get; init; }
    public string[] RequestedCapabilities { get; init; } = [];
    public PluginLimits Limits { get; init; } = new();
    public PluginToolDefinition[] Tools { get; init; } = [];
    public PluginHookDefinition[] Hooks { get; init; } = [];
    public PluginSubscriptionDefinition[] Subscriptions { get; init; } = [];
    public PluginAsset[] Assets { get; init; } = [];
}
public sealed record PluginTarget(string Framework = "net10.0", string Rid = "win-x64");
public sealed record PluginEntry(string Assembly, string Type);
public sealed record PluginLimits(int ExecutionTimeoutSeconds = 30, int MaxConcurrentCalls = 1);
public sealed record PluginAsset(string Path, string Kind = "text");
public sealed record PluginHookDefinition(string Id, string Event, int Order = 0);
/// <summary>Host workspace event subscription. Events are delivered outside any Agent session.</summary>
public sealed record PluginSubscriptionDefinition(string Id, string Event);

/// <summary>Host workspace events a plugin may subscribe to; the allowlist is the contract.</summary>
public static class PluginEvents
{
    public const string WorkspaceArtifactCreated = "workspace.artifact.created";
    public static readonly string[] All = [WorkspaceArtifactCreated];
}

/// <summary>One delivered workspace event. <see cref="EventId"/> is the host idempotency key.</summary>
public sealed record PluginWorkspaceEvent(string EventId, string EventName, PluginScope Scope,
    JsonElement Payload, DateTimeOffset OccurredAt);

public sealed record PluginEventDelivery(string ActivationId, string PluginId, string SubscriptionId,
    bool Success, string? Error = null);
public sealed record PluginToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string Category { get; init; } = "General";
    public string[] Effects { get; init; } = ["none"];
    public required JsonElement InputSchema { get; init; }
    public required JsonElement OutputSchema { get; init; }
}

/// <summary>Host-authored identity. Plugins cannot select another agent or session.</summary>
/// <param name="ActivationKey">Host-scoped activation identity (e.g. a workspace subscriber). When set, AgentId/SessionId may be empty.</param>
public sealed record PluginScope(string WorkspaceId, string AgentId, string SessionId, string? ActivationKey = null);
public sealed record PluginCall(string CallId, string ToolId, JsonElement Arguments,
    PluginScope Scope, DateTimeOffset Deadline);
public sealed record PluginResult(bool Success, JsonElement Value, string? Error = null)
{
    public static PluginResult Ok<T>(T value) => new(true, PluginJson.Value(value));
    public static PluginResult Fail(string error) => new(false, PluginJson.Value(new { }), error);
}
public sealed record PluginHookCall(string HookId, string ToolName, string Arguments,
    bool Success, string Output, string? Error, int ExitCode, PluginScope Scope);
/// <summary>Advisory result only. Execution outcome and original evidence cannot be replaced.</summary>
public sealed record PluginHookResult(string? AppendText = null);

public interface IToolPlugin
{
    ValueTask InitializeAsync(IPluginContext context, CancellationToken ct);
    IReadOnlyCollection<string> ToolIds { get; }
    IReadOnlyCollection<string> HookIds => [];
    IReadOnlyCollection<string> SubscriptionIds => [];
    ValueTask<PluginResult> ExecuteAsync(PluginCall call, CancellationToken ct);
    ValueTask<PluginHookResult> OnToolResultAsync(PluginHookCall call, CancellationToken ct)
        => ValueTask.FromResult(new PluginHookResult());
    ValueTask OnWorkspaceEventAsync(PluginWorkspaceEvent workspaceEvent, CancellationToken ct)
        => ValueTask.CompletedTask;
    ValueTask ShutdownAsync(CancellationToken ct) => ValueTask.CompletedTask;
}
/// <summary>
/// Authorized archive request exposed to a workspace observer. The host derives the archive identity (event id, plugin,
/// workspace, artifact reference) from its own context; the plugin only asks to archive the delivered event.
/// </summary>
public interface IPluginEventAudit
{
    ValueTask<bool> RequestArchiveAsync(string eventId, CancellationToken ct = default);
}

public interface IPluginContext
{
    PluginScope Scope { get; }
    IPluginCoreApi Core { get; }
}
/// <summary>Versioned, bounded host services; never exposes IServiceProvider, tokens or database objects.</summary>
public interface IPluginCoreApi
{
    ValueTask<string> ReadWorkspaceTextAsync(string relativePath, CancellationToken ct = default);
    ValueTask<string> ReadAssetTextAsync(string relativePath, CancellationToken ct = default);
    ValueTask<string?> ReadStateAsync(string key, CancellationToken ct = default);
    ValueTask WriteStateAsync(string key, string value, CancellationToken ct = default);
}
public sealed record PluginPackage(PluginManifest Manifest, string ContentHash, string Directory);
public sealed record PluginFailure(string Code, string Stage, string ErrorId, string Message,
    string EffectOutcome = "none");
public sealed class PluginException : Exception
{
    public PluginFailure Failure { get; }
    public PluginException(string code, string stage, string message, string effectOutcome = "none")
        : this(new PluginFailure(code, stage, Guid.NewGuid().ToString("N"), message, effectOutcome)) { }
    public PluginException(PluginFailure failure) : base(failure.Message) => Failure = failure;
}
public sealed record PluginWorkerRequest(string Kind, JsonElement Payload, DateTimeOffset Deadline);
public interface IPluginWorker : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    bool StopWasForced => true;
    Task<JsonElement> RequestAsync(PluginWorkerRequest request,
        Func<string, JsonElement, CancellationToken, Task<JsonElement>> callback, CancellationToken ct);
    Task<bool> StopAsync(CancellationToken ct);
}
public interface IPluginWorkerFactory
{
    Task<IPluginWorker> StartAsync(PluginPackage package, PluginScope scope, string activationId, CancellationToken ct);
}
