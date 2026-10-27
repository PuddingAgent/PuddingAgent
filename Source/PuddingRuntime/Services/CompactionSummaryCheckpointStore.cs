namespace PuddingRuntime.Services;

/// <summary>Bounded session-scoped preparation results. A cancelled turn can reuse admitted
/// segments on its next attempt; no partial candidate is ever applied to conversation history.
/// Keys include session, route and exact summary input (including its budget instruction).
/// Stores only text, never provider usage or invocation identity, so reuse cannot double-charge.</summary>
internal sealed class CompactionSummaryCheckpointStore(
    int maxEntries = 512, int maxCharacters = 8_000_000, TimeProvider? clock = null)
{
    private sealed record Entry(string Text, DateTimeOffset ExpiresAt, long Access);
    private readonly Dictionary<(string Scope, string Input), Entry> _entries = [];
    private readonly object _sync = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _access;
    private int _characters;

    internal string? Find(string scope, string inputHash)
    {
        lock (_sync)
        {
            Prune();
            var key = (scope, inputHash);
            if (!_entries.TryGetValue(key, out var entry)) return null;
            _entries[key] = entry with { Access = ++_access };
            return entry.Text;
        }
    }

    internal void Save(string scope, string inputHash, string text)
    {
        if (maxEntries <= 0 || text.Length > maxCharacters) return;
        lock (_sync)
        {
            Prune();
            var key = (scope, inputHash);
            Remove(key);
            while (_entries.Count > 0 && (_entries.Count >= maxEntries || _characters + text.Length > maxCharacters))
                Remove(_entries.MinBy(pair => pair.Value.Access).Key);
            _entries[key] = new(text, _clock.GetUtcNow().AddMinutes(30), ++_access);
            _characters += text.Length;
        }
    }

    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var key in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            Remove(key);
    }

    private void Remove((string Scope, string Input) key)
    {
        if (_entries.Remove(key, out var entry)) _characters -= entry.Text.Length;
    }
}
