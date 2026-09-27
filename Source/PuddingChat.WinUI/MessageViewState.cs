namespace PuddingChat.WinUI;

/// <summary>Non-visual state retained while a message card is recycled.</summary>
public sealed class MessageViewState
{
    internal string? RunId;
    internal readonly Dictionary<string, ProcessItem> Events = [];
    internal readonly Dictionary<string, bool> Expansions = [];
    internal readonly Dictionary<string, bool> Images = [];
    internal ProcessDetails? Details;
    internal bool DetailsExpanded;
    internal readonly FlowWindow FlowWindow = new();
    internal readonly FlowWindow DetailWindow = new();
}
