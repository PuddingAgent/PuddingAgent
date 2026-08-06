using System.Text.Json.Serialization;

namespace PuddingRuntime.Models;

/// <summary>
/// Goal 模式队列持久化状态。
/// 写入 {AgentInstanceRoot(agentId)}/goal_queue.json，重启后可恢复。
/// 语义：消费式队列——每次成功注入后游标前进；目标注入次数达到上限自动跳过（熔断）。
/// </summary>
public sealed class GoalQueueState
{
    [JsonPropertyName("agent_id")]
    public string AgentId { get; set; } = "";

    /// <summary>目标列表（按序消费）。</summary>
    [JsonPropertyName("goals")]
    public List<GoalEntry> Goals { get; set; } = new();

    /// <summary>消费游标：指向下一个待注入目标。</summary>
    [JsonPropertyName("cursor")]
    public int Cursor { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>队列中的单个目标条目。</summary>
public sealed class GoalEntry
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>已注入次数（正常为 1；重试/竞态下可能大于 1）。</summary>
    [JsonPropertyName("injection_count")]
    public int InjectionCount { get; set; }

    /// <summary>pending / injected / skipped。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "pending";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }
}