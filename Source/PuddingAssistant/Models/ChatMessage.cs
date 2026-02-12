namespace PuddingAssistant.Models;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>对话消息</summary>
public sealed record ChatMessage(
    ChatRole Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ReasoningContent = null);
