using System.Collections.Immutable;

namespace PuddingInvocationPipeline.Contracts;

// PA-07 07B 扩展端口（BCL-only）：Observer/Advice/Transform/Guard 分类、声明式 grant、代际围栏与不可变快照。
// 插件不能修改 tool call 身份/授权/真实退出码/预算归属，也不能把失败改成成功；前端展示与模型内容贡献分开授权。

public enum ExtensionKind { Observer, Advice, Transform, Guard }

public static class ExtensionPoints
{
    public const string ToolBefore = "tool.before";
    public const string ToolResult = "tool.result";
    public const string TurnStart = "turn.start";
    public const string TurnSettled = "turn.settled";

    public static bool Known(string? point) => point is ToolBefore or ToolResult or TurnStart or TurnSettled;
}

public sealed record ExtensionDescriptor(string ExtensionId, string PluginId, long Generation, string Point,
    ExtensionKind Kind, int Priority, string Version, ImmutableArray<string> Grants)
{
    public bool IsValid => Valid(ExtensionId) && Valid(PluginId) && Generation >= 1 && ExtensionPoints.Known(Point)
        && Priority is >= -1000 and <= 1000 && Valid(Version) && !Grants.IsDefault;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record ExtensionSnapshot(ImmutableArray<ExtensionDescriptor> Extensions, long Generation);

public interface IExtensionSource
{
    ValueTask<ExtensionSnapshot> LoadAsync(string point, CancellationToken ct = default);
}

/// <summary>Host-bound invocation identity; grants are host-owned and cannot be widened by a plugin.</summary>
public sealed record ExtensionContext(string WorkspaceId, string AgentId, string SessionId, string Point,
    string ToolCallId, string Capability, ImmutableArray<string> GrantedCapabilities);

public sealed record ExtensionPlan(ImmutableArray<ExtensionDescriptor> Ordered, ImmutableArray<ExtensionDescriptor> Skipped);

/// <summary>Fields a transform must never change; tool identity, authorization, real exit code, budget owner.</summary>
public static class ProtectedToolFields
{
    public const string ToolCallId = "toolCallId";
    public const string AuthorizationDecision = "authorizationDecision";
    public const string ExitCode = "exitCode";
    public const string BudgetOwner = "budgetOwner";
    public const string FailureCode = "failureCode";

    public static readonly ImmutableArray<string> All =
        [ToolCallId, AuthorizationDecision, ExitCode, BudgetOwner, FailureCode];
}

public static class ExtensionErrorCodes
{
    public const string DescriptorInvalid = "extension.descriptor_invalid";
    public const string Duplicate = "extension.duplicate";
    public const string GenerationStale = "extension.generation_stale";
    public const string GrantDenied = "extension.grant_denied";
    public const string TransformForbidden = "extension.transform_forbidden";
    public const string TransformDenied = "extension.transform_denied";
    public const string PointUnknown = "extension.point_unknown";
    public const string SourceUnavailable = "extension.source_unavailable";
}

public sealed class ExtensionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
