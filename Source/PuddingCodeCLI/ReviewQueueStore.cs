using System.Text.Json;

namespace PuddingCodeCLI;

public sealed record ReviewQueueItem(
    int Id,
    string Summary,
    string Decision,
    int ChangedFiles,
    List<string> PreviewLines,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    string? Note,
    string? ApprovedSnapshotHash);

public sealed class ReviewQueueStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public ReviewQueueStore(string projectRoot)
    {
        _path = Path.Combine(projectRoot, ".pudding", "review", "queue.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public IReadOnlyList<ReviewQueueItem> List()
    {
        lock (_gate)
        {
            return LoadInternal()
                .OrderByDescending(x => x.Id)
                .ToList();
        }
    }

    public ReviewQueueItem? Get(int id)
    {
        lock (_gate)
        {
            return LoadInternal().FirstOrDefault(x => x.Id == id);
        }
    }

    public ReviewQueueItem? GetLatest()
    {
        lock (_gate)
        {
            return LoadInternal().OrderByDescending(x => x.Id).FirstOrDefault();
        }
    }

    public ReviewQueueItem AddPending(string summary, int changedFiles, List<string> previewLines)
    {
        lock (_gate)
        {
            var items = LoadInternal();
            var id = items.Count == 0 ? 1 : items.Max(x => x.Id) + 1;
            var item = new ReviewQueueItem(
                Id: id,
                Summary: summary.Trim(),
                Decision: "pending",
                ChangedFiles: changedFiles,
                PreviewLines: previewLines,
                CreatedAt: DateTimeOffset.Now,
                ReviewedAt: null,
                Note: null,
                ApprovedSnapshotHash: null);
            items.Add(item);
            SaveInternal(items);
            return item;
        }
    }

    public ReviewQueueItem? SetDecision(int id, string decision, string? note = null, string? approvedSnapshotHash = null)
    {
        lock (_gate)
        {
            var items = LoadInternal();
            var idx = items.FindIndex(x => x.Id == id);
            if (idx < 0) return null;
            var old = items[idx];
            var updated = old with
            {
                Decision = decision,
                ReviewedAt = DateTimeOffset.Now,
                Note = note,
                ApprovedSnapshotHash = approvedSnapshotHash ?? old.ApprovedSnapshotHash
            };
            items[idx] = updated;
            SaveInternal(items);
            return updated;
        }
    }

    private List<ReviewQueueItem> LoadInternal()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<ReviewQueueItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveInternal(List<ReviewQueueItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
