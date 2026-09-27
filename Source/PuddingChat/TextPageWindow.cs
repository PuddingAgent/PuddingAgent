namespace PuddingChat;

/// <summary>Bounded presentation pages; the canonical source remains intact.</summary>
public sealed class TextPageWindow
{
    public const int PageSize = 16 * 1024;
    public const int LargeTextThreshold = 32 * 1024;
    private readonly List<int> _starts = [0];
    public string Source { get; private set; } = "";
    public int Page { get; private set; }
    public int Count => _starts.Count;
    public string Text => Source[_starts[Page]..(Page + 1 < Count ? _starts[Page + 1] : Source.Length)];
    public void Update(string source)
    {
        Source = source;
        _starts.Clear(); _starts.Add(0);
        var start = 0;
        while (source.Length - start > PageSize)
        {
            var end = start + PageSize;
            // Keep UTF-16 pairs and Windows line endings together across pages.
            if ((char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end]))
                || (source[end - 1] == '\r' && source[end] == '\n')) end++;
            if (end >= source.Length) break;
            _starts.Add(end); start = end;
        }
        Page = Math.Min(Page, Count - 1);
    }
    public void Move(int page) => Page = Math.Clamp(page, 0, Count - 1);
}
