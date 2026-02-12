using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PuddingAssistantDesktop.Services;

namespace PuddingAssistantDesktop.Models;

/// <summary>Role of an agent in the swarm hierarchy.</summary>
public enum AgentRole
{
    Leader,
    Worker
}

/// <summary>Runtime status of an agent.</summary>
public enum AgentStatus
{
    Idle,
    Thinking,
    ToolExecuting,
    Completed,
    Error,
    Offline,
    Sleeping,
    Rebuilding
}

/// <summary>A single log entry for an agent.</summary>
public sealed class AgentLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Level { get; init; } = "INFO";
    public string Message { get; init; } = "";
    public override string ToString() => $"[{Timestamp:HH:mm:ss}] {Level}: {Message}";
}

/// <summary>A single step in the agent's chain-of-thought.</summary>
public sealed class ThinkingStep
{
    public int StepIndex { get; init; }
    public string Content { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Label => $"Step {StepIndex}";
    public string TimeLabel => Timestamp.ToString("HH:mm:ss");
}

/// <summary>
/// A single agent node in the swarm topology graph.
/// Observable so the topology UI updates in real-time.
/// </summary>
public partial class SwarmAgent : ObservableObject
{
    [ObservableProperty] private string _id = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private AgentRole _role = AgentRole.Worker;
    [ObservableProperty] private AgentStatus _status = AgentStatus.Idle;
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string? _currentTask;
    [ObservableProperty] private string? _parentId;
    [ObservableProperty] private int _tokensUsed;
    [ObservableProperty] private string _lastMessage = "";

    /// <summary>
    /// Worker specialization tag used for Leader's smart routing.
    /// Examples: "Coder", "Researcher", "Analyst", "Tester", "Reporter".
    /// </summary>
    [ObservableProperty] private string _specialization = "General";

    // ──── Selection ────

    [ObservableProperty] private bool _isSelected;

    /// <summary>Border thickness when selected vs normal (topology nodes).</summary>
    public double SelectionBorderThickness => IsSelected ? 3.0 : 1.5;

    /// <summary>Border color override: cyan highlight when selected, otherwise status color.</summary>
    public string TopologyBorderColor => IsSelected ? "#00D4FF" : BorderColor;

    /// <summary>Leader node border thickness (thicker base).</summary>
    public double LeaderSelectionBorderThickness => IsSelected ? 4.0 : 2.0;

    partial void OnIsSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(SelectionBorderThickness));
        OnPropertyChanged(nameof(LeaderSelectionBorderThickness));
        OnPropertyChanged(nameof(TopologyBorderColor));
    }

    // ──── Thinking Chain ────

    /// <summary>Chain-of-thought steps for this agent.</summary>
    public ObservableCollection<ThinkingStep> ThinkingChain { get; } = new();

    // ──── Agent Log ────

    /// <summary>Runtime log entries for this agent.</summary>
    public ObservableCollection<AgentLogEntry> AgentLogs { get; } = new();

    /// <summary>Append a log entry.</summary>
    public void Log(string message, string level = "INFO")
    {
        AgentLogs.Add(new AgentLogEntry { Message = message, Level = level });
        PuddingLogger.SwarmTrace($"[AgentLog] {Id}: [{level}] {message}");
    }

    /// <summary>Append a thinking step.</summary>
    public void AddThinkingStep(string content)
    {
        var step = new ThinkingStep
        {
            StepIndex = ThinkingChain.Count + 1,
            Content = content
        };
        ThinkingChain.Add(step);
        PuddingLogger.SwarmTrace($"[ThinkingChain] {Id}: step {step.StepIndex} — {content}");
    }

    /// <summary>Formatted model parameters for display.</summary>
    public string ModelParamsDisplay =>
        $"Model: {Model}\nRole: {Role}\nStatus: {StatusLabel}\nTokens Used: {TokensUsed}\nMessages: {MessageHistory.Count}\nThinking Steps: {ThinkingChain.Count}";

    // ──── Bubble System ────

    /// <summary>The most recent bubble displayed near this agent node.</summary>
    [ObservableProperty] private SwarmBubble? _activeBubble;

    /// <summary>Number of unread messages.</summary>
    [ObservableProperty] private int _unreadCount;

    /// <summary>Full message history for this agent.</summary>
    public ObservableCollection<SwarmBubble> MessageHistory { get; } = new();

    public bool HasActiveBubble => ActiveBubble is not null;
    public bool HasUnread => UnreadCount > 0;
    public string UnreadBadge => UnreadCount > 9 ? "9+" : UnreadCount.ToString();

    /// <summary>Push a new bubble onto this agent.</summary>
    public void PushBubble(SwarmBubble bubble)
    {
        MessageHistory.Add(bubble);
        ActiveBubble = bubble;
        UnreadCount++;
        PuddingLogger.SwarmTrace($"[Bubble] {Id}: from={bubble.SenderName} kind={bubble.Kind} unread={UnreadCount}");
    }

    /// <summary>Mark all messages as read and dismiss the active bubble.</summary>
    public void MarkAllRead()
    {
        var count = UnreadCount;
        foreach (var b in MessageHistory)
            b.IsRead = true;
        UnreadCount = 0;
        ActiveBubble = null;
        PuddingLogger.SwarmTrace($"[Bubble] {Id}: MarkAllRead — {count} messages marked");
    }

    /// <summary>Dismiss the floating bubble without marking as read.</summary>
    public void DismissBubble() => ActiveBubble = null;

    partial void OnActiveBubbleChanged(SwarmBubble? value)
        => OnPropertyChanged(nameof(HasActiveBubble));

    partial void OnUnreadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasUnread));
        OnPropertyChanged(nameof(UnreadBadge));
    }

    /// <summary>Color for status indicator in the UI.</summary>
    public string StatusColor => Status switch
    {
        AgentStatus.Idle => "#888888",
        AgentStatus.Thinking => "#F5A623",
        AgentStatus.ToolExecuting => "#4A90D9",
        AgentStatus.Completed => "#7ED321",
        AgentStatus.Error => "#D0021B",
        AgentStatus.Offline => "#444444",
        AgentStatus.Sleeping => "#9B59B6",
        AgentStatus.Rebuilding => "#E67E22",
        _ => "#888888"
    };

    /// <summary>Glow color for the node card border (brighter).</summary>
    public string GlowColor => Status switch
    {
        AgentStatus.Idle => "#55888888",
        AgentStatus.Thinking => "#88F5A623",
        AgentStatus.ToolExecuting => "#884A90D9",
        AgentStatus.Completed => "#887ED321",
        AgentStatus.Error => "#88D0021B",
        AgentStatus.Offline => "#33444444",
        AgentStatus.Sleeping => "#889B59B6",
        AgentStatus.Rebuilding => "#88E67E22",
        _ => "#55888888"
    };

    /// <summary>Node border color — stronger version of status color.</summary>
    public string BorderColor => Status switch
    {
        AgentStatus.Idle => "#555555",
        AgentStatus.Thinking => "#F5A623",
        AgentStatus.ToolExecuting => "#4A90D9",
        AgentStatus.Completed => "#7ED321",
        AgentStatus.Error => "#D0021B",
        AgentStatus.Offline => "#333333",
        AgentStatus.Sleeping => "#9B59B6",
        AgentStatus.Rebuilding => "#E67E22",
        _ => "#555555"
    };

    /// <summary>Connector line color to leader.</summary>
    public string ConnectorColor => Status switch
    {
        AgentStatus.Thinking => "#F5A623",
        AgentStatus.ToolExecuting => "#4A90D9",
        AgentStatus.Completed => "#7ED321",
        AgentStatus.Error => "#D0021B",
        AgentStatus.Sleeping => "#9B59B6",
        AgentStatus.Rebuilding => "#E67E22",
        _ => "#444444"
    };

    /// <summary>Icon for the agent role.</summary>
    public string RoleIcon => Role switch
    {
        AgentRole.Leader => "👑",
        AgentRole.Worker => "🐝",
        _ => "•"
    };

    public string StatusLabel => Status switch
    {
        AgentStatus.Idle => "Idle",
        AgentStatus.Thinking => "Thinking...",
        AgentStatus.ToolExecuting => "Executing...",
        AgentStatus.Completed => "Done",
        AgentStatus.Error => "Error",
        AgentStatus.Offline => "Offline",
        AgentStatus.Sleeping => "Sleeping",
        AgentStatus.Rebuilding => "Rebuilding...",
        _ => "Unknown"
    };

    public double StatusOpacity => Status switch
    {
        AgentStatus.Offline => 0.4,
        AgentStatus.Idle => 0.7,
        AgentStatus.Sleeping => 0.5,
        _ => 1.0
    };

    partial void OnStatusChanged(AgentStatus oldValue, AgentStatus newValue)
    {
        PuddingLogger.SwarmTrace($"[State] {Id}: {oldValue} → {newValue}");
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(GlowColor));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(TopologyBorderColor));
        OnPropertyChanged(nameof(ConnectorColor));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusOpacity));
        OnPropertyChanged(nameof(ModelParamsDisplay));
    }

    partial void OnTokensUsedChanged(int value)
        => OnPropertyChanged(nameof(ModelParamsDisplay));
}
