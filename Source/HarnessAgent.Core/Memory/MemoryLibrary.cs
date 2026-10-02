using System.Text.RegularExpressions;

namespace HarnessAgent.Core.Memory;

/// <summary>
/// Memory library — persistent, searchable memory with FTS5-inspired text search.
/// The core layer of the 6-layer memory architecture.
/// </summary>
public sealed class MemoryLibrary
{
    private readonly Dictionary<string, MemoryBook> _books = new();
    private readonly List<MemoryEntry> _entries = new();
    private readonly List<MemoryRelation> _relations = new();
    private readonly object _lock = new();

    // ── Book Management ──

    public MemoryBook CreateBook(string title, string summary = "", IReadOnlySet<string>? tags = null)
    {
        var book = new MemoryBook
        {
            BookId = $"book_{Guid.NewGuid():N}"[..16],
            Title = title,
            Summary = summary,
            Tags = tags ?? new HashSet<string>(),
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        };
        lock (_lock) _books[book.BookId] = book;
        return book;
    }

    public IReadOnlyList<MemoryBook> ListBooks()
    {
        lock (_lock) return _books.Values.ToList();
    }

    public MemoryBook? GetBook(string bookId)
    {
        lock (_lock) return _books.GetValueOrDefault(bookId);
    }

    // ── Entry Management ──

    public MemoryEntry AddEntry(MemoryBook book, MemoryKind kind, string content, string title = "",
        IReadOnlySet<string>? tags = null, int priority = 0)
    {
        var entry = new MemoryEntry
        {
            EntryId = $"mem_{Guid.NewGuid():N}"[..16],
            Kind = kind,
            Title = title,
            Content = content,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Tags = tags ?? new HashSet<string>(),
            Priority = priority,
        };
        lock (_lock) _entries.Add(entry);
        return entry;
    }

    public MemoryEntry? GetEntry(string entryId)
    {
        lock (_lock) return _entries.Find(e => e.EntryId == entryId && !e.IsArchived);
    }

    public IReadOnlyList<MemoryEntry> GetEntriesByBook(MemoryBook book)
    {
        lock (_lock) return _entries
            .Where(e => !e.IsArchived)
            .ToList();
    }

    public IReadOnlyList<MemoryEntry> GetEntriesByKind(MemoryKind kind)
    {
        lock (_lock) return _entries
            .Where(e => e.Kind == kind && !e.IsArchived)
            .OrderByDescending(e => e.Priority)
            .ToList();
    }

    // ── Search ──

    /// <summary>Full-text search (simple FTS5-inspired token matching).</summary>
    public IReadOnlyList<MemoryEntry> Search(string query, int maxResults = 20)
    {
        var tokens = Tokenize(query);
        if (tokens.Length == 0) return new List<MemoryEntry>();

        lock (_lock)
        {
            return _entries
                .Where(e => !e.IsArchived && tokens.Any(t =>
                    e.Content.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    e.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    e.Tags.Any(tag => tag.Contains(t, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(e => tokens.Count(t =>
                    e.Content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                .ThenByDescending(e => e.Priority)
                .Take(maxResults)
                .ToList();
        }
    }

    /// <summary>Regex search for advanced queries.</summary>
    public IReadOnlyList<MemoryEntry> SearchRegex(string pattern, int maxResults = 20)
    {
        lock (_lock)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            return _entries
                .Where(e => !e.IsArchived && regex.IsMatch(e.Content + " " + e.Title))
                .Take(maxResults)
                .ToList();
        }
    }

    // ── Relations ──

    public MemoryRelation AddRelation(string sourceId, string targetId, string relationType,
        string description = "", double weight = 1.0)
    {
        var rel = new MemoryRelation
        {
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Description = description,
            Weight = weight,
        };
        lock (_lock) _relations.Add(rel);
        return rel;
    }

    public IReadOnlyList<MemoryRelation> GetRelations(string entryId)
    {
        lock (_lock) return _relations
            .Where(r => r.SourceId == entryId || r.TargetId == entryId)
            .ToList();
    }

    // ── Stats ──

    public (int Books, int Entries, int Relations, int EstimatedTokens) GetStats()
    {
        lock (_lock) return (_books.Count, _entries.Count, _relations.Count,
            _entries.Sum(e => e.EstimatedTokens));
    }

    // ── Internal ──

    private static string[] Tokenize(string query)
    {
        return Regex.Split(query, @"\W+")
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }
}
