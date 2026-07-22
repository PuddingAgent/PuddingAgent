namespace PuddingCode.Models;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>
/// 对话消息。当 VisualArtifactIds 非空时，"User" 角色的消息将被渲染为
/// OpenAI vision 多模态格式（text + image_url content 数组）。
/// </summary>
public sealed record ChatMessage(
    ChatRole Role,
    string? Content,
    string? ToolCallId = null,
    string? ToolName = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ReasoningContent = null,
    IReadOnlyList<string>? VisualArtifactIds = null);
