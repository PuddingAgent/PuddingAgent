namespace PuddingChat;

/// <summary>Presentation disclosure only; never removes canonical activity data.</summary>
public sealed class FlowWindow
{
    public const int InitialCount = 40;
    public const int PageSize = 24;
    private string? _firstRevealed;
    public int Start(IReadOnlyList<FlowBlock> blocks)
    {
        if (_firstRevealed is not null)
            for (var i = 0; i < blocks.Count; i++) if (blocks[i].Key == _firstRevealed) return i;
        return Math.Max(0, blocks.Count - InitialCount);
    }
    public void RevealEarlier(IReadOnlyList<FlowBlock> blocks)
    {
        if (blocks.Count > 0) _firstRevealed = blocks[Math.Max(0, Start(blocks) - PageSize)].Key;
    }
    public void Reset() => _firstRevealed = null;
}
