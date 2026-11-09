using System.Text.Json.Serialization;

namespace Pudding.ToolPlugins.AgentSdk;

// PA-07 07B 插件后台任务契约（冻结）：插件只收到宿主已授权的任务信封；幂等键由宿主绑定，
// 结果状态显式（Unknown 不等于成功/失败），插件不能自行重试或伪造 Replay。
// 真实任务存储/租约/重试由宿主 Worker 负责；这里不暴露任何 Host 服务。

public enum PluginJobState { Queued, Running, Completed, Failed, Unknown, Cancelled }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginJobEnvelope(string JobId, string PluginId, long Generation, string Kind, string PayloadJson,
    string IdempotencyKey, DateTimeOffset Deadline, int MaxAttempts)
{
    public bool IsValid => Valid(JobId) && Valid(PluginId) && Generation >= 1 && Valid(Kind)
        && PayloadJson.Length <= 1_048_576 && Valid(IdempotencyKey) && Deadline > DateTimeOffset.UtcNow
        && MaxAttempts is >= 1 and <= 8;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

/// <summary>A job result. A replay result means the host deduplicated an idempotent run; the plugin must not claim it.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PluginJobResult(PluginJobState State, string Summary, string? FailureCode, bool Replay = false)
{
    public bool IsValid => State != PluginJobState.Running
        && Summary.Length <= 4096
        && (State != PluginJobState.Failed || (!string.IsNullOrWhiteSpace(FailureCode) && FailureCode.Length <= 128))
        && (State != PluginJobState.Completed || string.IsNullOrWhiteSpace(FailureCode));
}

public interface IPluginJobHandler
{
    Task<PluginJobResult> RunAsync(PluginJobEnvelope envelope, CancellationToken ct = default);
}

public sealed class PluginJobsException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
