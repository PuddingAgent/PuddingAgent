using System.Text.Json.Serialization;

namespace PuddingRuntime.Models;

/// <summary>
/// Agent 心跳频率持久化偏好。
/// 写入 {AgentInstanceRoot(agentId)}/heartbeat.json。
/// 启动时 HeartbeatOrchestrator 读取恢复。
/// </summary>
public sealed class HeartbeatPreference
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; init; } = "";

    [JsonPropertyName("min_idle_seconds")]
    public int MinIdleSeconds { get; init; }

    [JsonPropertyName("max_idle_seconds")]
    public int MaxIdleSeconds { get; init; }

    [JsonPropertyName("work_summary")]
    public string? WorkSummary { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }
}
