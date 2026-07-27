using System.Text.Json;
using System.Text.Json.Serialization;
using PuddingCodexService.Models;

namespace PuddingCodexService.Services;

public sealed partial class FileCodexTaskStore(CodexServiceOptions options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = options.TaskStoreDirectory;

    public async Task<CodexTaskRecord> CreateAsync(CodexTaskRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = GetPath(record.TaskId);
            if (File.Exists(path))
                throw new InvalidOperationException($"Codex task already exists: {record.TaskId}");
            await WriteAtomicAsync(path, record, ct);
            return record;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexTaskRecord?> GetAsync(string taskId, CancellationToken ct = default)
    {
        ValidateTaskId(taskId);
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnsafeAsync(GetPath(taskId), ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CodexTaskRecord>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var records = new List<CodexTaskRecord>();
            foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
            {
                var record = await ReadUnsafeAsync(path, ct);
                if (record is not null)
                    records.Add(record);
            }

            return records.OrderBy(record => record.CreatedAtUtc).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CodexTaskRecord> UpdateAsync(
        string taskId,
        Func<CodexTaskRecord, CodexTaskRecord> update,
        CancellationToken ct = default)
    {
        ValidateTaskId(taskId);
        await _gate.WaitAsync(ct);
        try
        {
            var path = GetPath(taskId);
            var current = await ReadUnsafeAsync(path, ct)
                          ?? throw new KeyNotFoundException($"Codex task was not found: {taskId}");
            var next = update(current) with
            {
                TaskId = current.TaskId,
                Revision = current.Revision + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            await WriteAtomicAsync(path, next, ct);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string taskId)
    {
        ValidateTaskId(taskId);
        return Path.Combine(_root, $"{taskId}.json");
    }

    private static void ValidateTaskId(string taskId)
    {
        if (!Guid.TryParseExact(taskId, "N", out _))
            throw new ArgumentException("Codex taskId must be a 32-character GUID.", nameof(taskId));
    }

    private static async Task<CodexTaskRecord?> ReadUnsafeAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync(stream, CodexTaskJsonContext.Default.CodexTaskRecord, ct);
    }

    private static async Task WriteAtomicAsync(string path, CodexTaskRecord record, CancellationToken ct)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, record, CodexTaskJsonContext.Default.CodexTaskRecord, ct);
                await stream.FlushAsync(ct);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
    [JsonSerializable(typeof(CodexTaskRecord))]
    private sealed partial class CodexTaskJsonContext : JsonSerializerContext;
}
