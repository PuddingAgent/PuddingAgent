using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PuddingCodeDesktop.Models;

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
    Offline
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
    }

    /// <summary>Mark all messages as read and dismiss the active bubble.</summary>
    public void MarkAllRead()
    {
        foreach (var b in MessageHistory)
            b.IsRead = true;
        UnreadCount = 0;
        ActiveBubble = null;
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
        _ => "#555555"
    };

    /// <summary>Connector line color to leader.</summary>
    public string ConnectorColor => Status switch
    {
        AgentStatus.Thinking => "#F5A623",
        AgentStatus.ToolExecuting => "#4A90D9",
        AgentStatus.Completed => "#7ED321",
        AgentStatus.Error => "#D0021B",
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
        _ => "Unknown"
    };

    public double StatusOpacity => Status switch
    {
        AgentStatus.Offline => 0.4,
        AgentStatus.Idle => 0.7,
        _ => 1.0
    };

    partial void OnStatusChanged(AgentStatus value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(GlowColor));
        OnPropertyChanged(nameof(BorderColor));
        OnPropertyChanged(nameof(ConnectorColor));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusOpacity));
    }
}
