using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pudding.ToolPlugins.AgentSdk;

// PA-07 07B 插件扩展点契约（冻结）：插件只声明 Observer/Advice/Transform/Guard 贡献；
// 分类、排序、grant、generation 与 protected field 校验由宿主实现（见 PuddingInvocationPipeline）。
// 插件不能自行放宽权限、伪造授权或改写失败诊断；这里只暴露声明与处理回调形状。

public enum PluginExtensionKind { Observer, Advice, Transform, Guard }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginExtensionDeclaration(string ExtensionId, string Point, PluginExtensionKind Kind,
    int Priority, string Version, bool RequiresApproval)
{
    public bool IsValid => Valid(ExtensionId) && Valid(Point) && Priority is >= -1000 and <= 1000 && Valid(Version);

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

/// <summary>Immutable, host-built inputs for one extension invocation; the plugin never selects the target.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginExtensionNotice(string Point, string ToolCallId, string Capability,
    ImmutableDictionary<string, string> Fields);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginTransformResult(bool Succeeded, ImmutableDictionary<string, string> Fields,
    string? FailureCode)
{
    /// <summary>The host rejects changes to tool identity/authorization/exit code/budget ownership; this shape only checks form.</summary>
    public bool IsValid => Fields is not null
        && (Succeeded ? string.IsNullOrWhiteSpace(FailureCode) : !string.IsNullOrWhiteSpace(FailureCode))
        && Fields.Count <= 64 && Fields.All(pair => pair.Key.Length is > 0 and <= 128 && pair.Value.Length <= 64_000);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginGuardDecision(bool Allowed, string? ReasonCode)
{
    public bool IsValid => Allowed ? string.IsNullOrWhiteSpace(ReasonCode)
        : !string.IsNullOrWhiteSpace(ReasonCode) && ReasonCode.Length <= 128;
}

public interface IPluginObserver
{
    Task ObserveAsync(PluginExtensionNotice notice, CancellationToken ct = default);
}

public interface IPluginAdvisor
{
    Task<string?> AdviseAsync(PluginExtensionNotice notice, CancellationToken ct = default);
}

public interface IPluginTransformer
{
    Task<PluginTransformResult> TransformAsync(PluginExtensionNotice notice, CancellationToken ct = default);
}

public interface IPluginGuard
{
    Task<PluginGuardDecision> EvaluateAsync(PluginExtensionNotice notice, CancellationToken ct = default);
}
