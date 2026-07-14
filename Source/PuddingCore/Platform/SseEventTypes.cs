namespace PuddingCode.Platform;

/// <summary>
/// SSE 事件类型常量（旧版）。
/// <para>
/// ADR-056-E：新代码请使用 SessionEventNames 命名约定。
/// 旧名称保留以保证向后兼容；SSE 输出自动映射旧名→新名（SessionEventNames.MapLegacy）。
/// </para>
/// </summary>
public static class SseEventTypes
{
    public const string Metadata = "metadata";
    public const string Thinking = "thinking";       // → SessionEventNames.AssistantThinkingDelta
    public const string Delta = "delta";             // → SessionEventNames.AssistantContentDelta
    public const string ToolCall = "tool_call";      // → SessionEventNames.ToolCallStarted
    public const string ToolResult = "tool_result";  // → SessionEventNames.ToolCallCompleted
    public const string Terminal = "terminal";
    public const string Usage = "usage";             // → SessionEventNames.UsageRecorded
    public const string Context = "context";
    public const string ContextHealth = "context.health";
    public const string ContextCompactionStarted = "context.compaction.started";
    public const string ContextCompactionCompleted = "context.compaction.completed";
    public const string ContextCompactionFailed = "context.compaction.failed";
    public const string VoiceCaptureStatus = "voice_capture_status";
    public const string VoicePlaybackStatus = "voice_playback_status";
    public const string CameraCaptureStatus = "camera_capture_status";
    public const string VisualReasoningStatus = "visual_reasoning_status";
    public const string Step = "step";
    public const string Done = "done";               // → SessionEventNames.TurnCompleted
    public const string Error = "error";             // → SessionEventNames.TurnFailed
    public const string Cancelled = "cancelled";     // → SessionEventNames.TurnCancelled
}
