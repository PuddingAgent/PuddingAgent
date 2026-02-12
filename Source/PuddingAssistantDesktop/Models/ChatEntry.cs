namespace PuddingAssistantDesktop.Models;

/// <summary>A single entry in the chat / thought stream view.</summary>
public sealed class ChatEntry
{
    public required ChatEntryKind Kind { get; init; }
    public required string Content { get; set; }
    public string? ToolName { get; init; }

    /// <summary>Whether this entry should be appended to (streaming in progress).</summary>
    public bool IsStreaming { get; set; }

    public string Icon => Kind switch
    {
        ChatEntryKind.User => "👤",
        ChatEntryKind.Thinking => "🍮",
        ChatEntryKind.ToolCall => "⚙️",
        ChatEntryKind.ToolResult => "📂",
        ChatEntryKind.Answer => "🤖",
        ChatEntryKind.Reasoning => "💭",
        ChatEntryKind.Error => "❌",
        ChatEntryKind.System => "ℹ️",
        _ => "•"
    };

    public override string ToString() => $"{Icon}  {Content}";
}

public enum ChatEntryKind
{
    User,
    Thinking,
    ToolCall,
    ToolResult,
    Answer,
    Reasoning,
    Error,
    System
}
