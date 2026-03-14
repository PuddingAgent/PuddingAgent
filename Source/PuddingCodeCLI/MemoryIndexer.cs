using System.Text.Json;
using System.Text.RegularExpressions;

namespace PuddingCodeCLI;

public sealed record MemorySearchResult(string Source, string Text, double Score);

public interface IMemoryIndexer
{
    Task RebuildAsync(IEnumerable<(string Source, string Path)> sources, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int topK = 3, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Local memory indexer with lightweight vector search.
/// v2 skeleton: no external embedding dependency, uses token-TF vectors + cosine similarity.
/// </summary>
public sealed class LocalMemoryIndexer : IMemoryIndexer
{
    private readonly string _indexPath;
    private readonly List<IndexedMemoryEntry> _entries = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalMemoryIndexer(string indexPath)
    {
        _indexPath = indexPath;
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
    }

    public async Task RebuildAsync(IEnumerable<(string Source, string Path)> sources, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _entries.Clear();

            foreach (var (source, path) in sources)
            {
                if (!File.Exists(path)) continue;
                var lines = await File.ReadAllLinesAsync(path, ct);
                foreach (var line in lines)
                {
                    var text = line.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (text.StartsWith("#")) continue;

                    _entries.Add(new IndexedMemoryEntry
                    {
                        Source = source,
                        Text = text
                    });
                }
            }

            await SaveIndexAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, int topK = 3, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        await _gate.WaitAsync(ct);
        try
        {
            if (_entries.Count == 0)
                await LoadIndexAsync(ct);

            var queryVec = BuildVector(query);
            if (queryVec.Count == 0) return [];

            var ranked = _entries
                .Select(e => new MemorySearchResult(e.Source, e.Text, Cosine(BuildVector(e.Text), queryVec)))
                .Where(r => r.Score > 0)
                .OrderByDescending(r => r.Score)
                .Take(Math.Max(1, topK))
                .ToList();

            return ranked;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_entries.Count == 0)
                await LoadIndexAsync(ct);

            return _entries.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var payload = new StoredIndex
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Entries = _entries.Select(e => new StoredEntry { Source = e.Source, Text = e.Text }).ToList()
        };

        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_indexPath, json, ct);
    }

    private async Task LoadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath)) return;

        var json = await File.ReadAllTextAsync(_indexPath, ct);
        var payload = JsonSerializer.Deserialize<StoredIndex>(json);
        if (payload?.Entries is null) return;

        _entries.Clear();
        _entries.AddRange(payload.Entries.Select(e => new IndexedMemoryEntry
        {
            Source = e.Source ?? "unknown",
            Text = e.Text ?? string.Empty
        }).Where(e => !string.IsNullOrWhiteSpace(e.Text)));
    }

    private static Dictionary<string, double> BuildVector(string text)
    {
        var terms = Tokenize(text);
        if (terms.Count == 0) return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        var vec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            vec.TryGetValue(term, out var current);
            vec[term] = current + 1;
        }

        var norm = Math.Sqrt(vec.Values.Sum(v => v * v));
        if (norm <= 0) return vec;

        foreach (var key in vec.Keys.ToList())
            vec[key] /= norm;

        return vec;
    }

    private static List<string> Tokenize(string text)
    {
        return Regex
            .Split(text.ToLowerInvariant(), "[^a-z0-9_\\u4e00-\\u9fa5]+")
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => t.Length >= 2)
            .ToList();
    }

    private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;

        var (smaller, larger) = a.Count <= b.Count ? (a, b) : (b, a);
        double dot = 0;
        foreach (var (k, v) in smaller)
        {
            if (larger.TryGetValue(k, out var lv))
                dot += v * lv;
        }

        return dot;
    }

    private sealed class IndexedMemoryEntry
    {
        public string Source { get; init; } = "unknown";
        public string Text { get; init; } = string.Empty;
    }

    private sealed class StoredIndex
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public List<StoredEntry> Entries { get; set; } = [];
    }

    private sealed class StoredEntry
    {
        public string? Source { get; set; }
        public string? Text { get; set; }
    }
}
