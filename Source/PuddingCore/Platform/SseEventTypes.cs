namespace PuddingCode.Platform;

/// <summary>SSE 事件类型常量——全局统一的事件名称定义。</summary>
public static class SseEventTypes
{
    public const string Metadata = "metadata";
    public const string Thinking = "thinking";    // 思维链增量
    public const string Delta = "delta";          // 回复文本增量
    public const string ToolCall = "tool_call";   // 工具调用开始
    public const string ToolResult = "tool_result"; // 工具调用结果
    public const string Terminal = "terminal";    // 终端代理输出
    public const string Usage = "usage";          // Token 用量
    public const string Context = "context";      // 上下文层 Token 占比
    public const string ContextHealth = "context.health"; // 上下文健康状态
    public const string ContextCompactionStarted = "context.compaction.started";
    public const string ContextCompactionCompleted = "context.compaction.completed";
    public const string ContextCompactionFailed = "context.compaction.failed";
    public const string VoiceCaptureStatus = "voice_capture_status";
    public const string VoicePlaybackStatus = "voice_playback_status";
    public const string CameraCaptureStatus = "camera_capture_status";
    public const string VisualReasoningStatus = "visual_reasoning_status";
    public const string Step = "step";            // 状态转换（向后兼容）
    public const string Done = "done";            // 正常结束
    public const string Error = "error";          // 错误终止
    public const string Cancelled = "cancelled";  // 用户取消
}
