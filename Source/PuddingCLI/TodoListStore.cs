using System.Text.Json;

namespace PuddingCodeCLI;

public sealed record TodoItem(int Id, string Title, bool Done, DateTimeOffset CreatedAt, DateTimeOffset? DoneAt);

public sealed class TodoListStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public TodoListStore(string projectRoot)
    {
        _path = Path.Combine(projectRoot, ".pudding", "todo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public IReadOnlyList<TodoItem> List()
    {
        lock (_gate)
        {
            return LoadInternal().OrderBy(t => t.Id).ToList();
        }
    }

    public TodoItem Add(string title)
    {
        lock (_gate)
        {
            var items = LoadInternal();
            var id = items.Count == 0 ? 1 : items.Max(t => t.Id) + 1;
            var item = new TodoItem(id, title.Trim(), false, DateTimeOffset.Now, null);
            items.Add(item);
            SaveInternal(items);
            return item;
        }
    }

    public TodoItem? MarkDone(int id)
    {
        lock (_gate)
        {
            var items = LoadInternal();
            var idx = items.FindIndex(t => t.Id == id);
            if (idx < 0) return null;
            var old = items[idx];
            var updated = old with { Done = true, DoneAt = DateTimeOffset.Now };
            items[idx] = updated;
            SaveInternal(items);
            return updated;
        }
    }

    public bool Remove(int id)
    {
        lock (_gate)
        {
            var items = LoadInternal();
            var removed = items.RemoveAll(t => t.Id == id) > 0;
            if (removed) SaveInternal(items);
            return removed;
        }
    }

    private List<TodoItem> LoadInternal()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<List<TodoItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveInternal(List<TodoItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}

