using CommunityToolkit.Mvvm.ComponentModel;

namespace PuddingAssistantDesktop.Models;

/// <summary>A single entry in the chat / thought stream view.</summary>
public sealed partial class ChatEntry : ObservableObject
{
    public required ChatEntryKind Kind { get; init; }

    /// <summary>Message text. Observable so streaming tokens update the UI in real-time.</summary>
    [ObservableProperty] private string _content = string.Empty;

    /// <summary>Reasoning / thinking chain text displayed above the answer in the same bubble.</summary>
    [ObservableProperty] private string _reasoningContent = string.Empty;

    public string? ToolName { get; init; }

    /// <summary>Whether this entry is still being streamed.</summary>
    [ObservableProperty] private bool _isStreaming;

    /// <summary>Whether this is a user message (right-aligned) vs agent message (left-aligned).</summary>
    public bool IsUser => Kind == ChatEntryKind.User;

    /// <summary>Whether this entry has reasoning content to display.</summary>
    public bool HasReasoning => !string.IsNullOrEmpty(ReasoningContent);

    public string Icon => Kind switch
    {
        ChatEntryKind.User => "👤",
        ChatEntryKind.Thinking => "🍮",
        ChatEntryKind.ToolCall => "⚙️",
        ChatEntryKind.ToolResult => "📂",
        ChatEntryKind.Answer => "🍮",
        ChatEntryKind.Reasoning => "💭",
        ChatEntryKind.Error => "❌",
        ChatEntryKind.System => "🍮",
        _ => "•"
    };

    partial void OnReasoningContentChanged(string? oldValue, string newValue)
    {
        OnPropertyChanged(nameof(HasReasoning));
    }

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
