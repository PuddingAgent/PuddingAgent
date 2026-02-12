using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PuddingAssistantDesktop.Models;

/// <summary>Emotion/type of a swarm bubble message.</summary>
public enum BubbleKind
{
    Info,
    Success,
    Blocked,
    Critique,
    Command
}

/// <summary>
/// A chat bubble floating near an Agent node in the Swarm topology.
/// </summary>
public partial class SwarmBubble : ObservableObject
{
    [ObservableProperty] private string _senderId = "";
    [ObservableProperty] private string _senderName = "";
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private BubbleKind _kind = BubbleKind.Info;
    [ObservableProperty] private DateTime _timestamp = DateTime.Now;
    [ObservableProperty] private bool _isRead;

    /// <summary>First 30 chars for bubble preview.</summary>
    public string Preview => Content.Length <= 30 ? Content : Content[..30] + "…";

    /// <summary>Bubble background color based on kind.</summary>
    public string BubbleBg => Kind switch
    {
        BubbleKind.Success => "#223A22",
        BubbleKind.Blocked => "#3A3322",
        BubbleKind.Critique => "#3A2222",
        BubbleKind.Command => "#2A2244",
        _ => "#222244"
    };

    /// <summary>Bubble border color based on kind.</summary>
    public string BubbleBorder => Kind switch
    {
        BubbleKind.Success => "#7ED321",
        BubbleKind.Blocked => "#F5A623",
        BubbleKind.Critique => "#D0021B",
        BubbleKind.Command => "#B388FF",
        _ => "#4A90D9"
    };

    /// <summary>Emoji prefix for the bubble.</summary>
    public string KindIcon => Kind switch
    {
        BubbleKind.Success => "✅",
        BubbleKind.Blocked => "⚠️",
        BubbleKind.Critique => "❗",
        BubbleKind.Command => "👑",
        _ => "💬"
    };

    public string TimeLabel => Timestamp.ToString("HH:mm:ss");

    partial void OnContentChanged(string value) => OnPropertyChanged(nameof(Preview));

    partial void OnKindChanged(BubbleKind value)
    {
        OnPropertyChanged(nameof(BubbleBg));
        OnPropertyChanged(nameof(BubbleBorder));
        OnPropertyChanged(nameof(KindIcon));
    }
}
