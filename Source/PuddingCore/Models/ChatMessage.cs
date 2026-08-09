namespace PuddingCode.Models;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>
/// 对话消息。当视觉/音频 Artifact ID 非空且当前模型声明对应能力时，
/// "User" 角色消息会被渲染为 OpenAI-compatible 多模态内容数组。
/// </summary>
public sealed record ChatMessage(
    ChatRole Role,
    string? Content,
    string? ToolCallId = null,
    string? ToolName = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ReasoningContent = null,
    IReadOnlyList<string>? VisualArtifactIds = null,
    IReadOnlyList<string>? AudioArtifactIds = null,
    LlmContinuationState? ContinuationState = null);
