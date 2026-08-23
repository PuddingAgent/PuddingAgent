namespace PuddingCode.Models;

public enum ChatRole { System, User, Assistant, Tool }

/// <summary>
/// 对话消息。图片事实（ADR-077）以有序 <see cref="ContentParts"/> 为 canonical：
/// 图片只保存 Artifact 引用，由 Provider invocation boundary 解析字节。
/// <see cref="VisualArtifactIds"/> 仅为旧构造路径的派生便利（音频链路继续使用 AudioArtifactIds）；
/// Gateway 渲染统一经过 <see cref="ChatMessageMultimodalNormalizer"/>，不存在双源漂移。
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
    LlmContinuationState? ContinuationState = null,
    IReadOnlyList<LlmContentPart>? ContentParts = null);
