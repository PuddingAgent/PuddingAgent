using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace PuddingContextHandoff;

/// <summary>Caller-supplied identities must come from the execution host, never model arguments.</summary>
public sealed record HandoffScope(string WorkspaceId, string SessionId, string AgentId);

/// <summary>A model-authored interpretation, not verified evidence or a new user instruction.</summary>
public sealed record HandoffRecord(
    string Id, HandoffScope Scope, string? RunId, string? ToolCallId, string? TurnId,
    DateTimeOffset CreatedAt, string Summary);

/// <summary>Bounded, immutable notes plus an atomically replaced latest pointer. No host dependencies.</summary>
public sealed class HandoffStore(string rootDirectory)
{
    public const int MaxSummaryBytes = 6000;
    private const int MaxRecordBytes = 7 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _root = Path.GetFullPath(rootDirectory);

    public async Task<HandoffRecord> SaveAsync(HandoffScope scope, string summary,
        string? runId = null, string? toolCallId = null, string? turnId = null, CancellationToken ct = default)
    {
        var directory = ScopeDirectory(scope);
        if (string.IsNullOrWhiteSpace(summary) || Encoding.UTF8.GetByteCount(summary) > MaxSummaryBytes)
            throw new ArgumentException($"Summary must contain 1–{MaxSummaryBytes} UTF-8 bytes; shorten it without dropping unresolved work.");
        var record = new HandoffRecord(Guid.NewGuid().ToString("N"), scope, runId, toolCallId, turnId,
            DateTimeOffset.UtcNow, summary.Trim());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        if (bytes.Length > MaxRecordBytes) throw new ArgumentException("Handoff serialized record exceeds the retrieval budget.");
        Directory.CreateDirectory(directory);
        // Publish the immutable record before the pointer. A failed/cancelled save never destroys the previous note.
        await AtomicWriteAsync(Path.Combine(directory, record.Id + ".json"), bytes, overwrite: false, ct);
        await AtomicWriteAsync(Path.Combine(directory, "latest"), Encoding.ASCII.GetBytes(record.Id), overwrite: true, ct);
        return record;
    }

    public async Task<HandoffRecord?> ReadAsync(HandoffScope scope, string? id = null, CancellationToken ct = default)
    {
        var directory = ScopeDirectory(scope);
        if (id is null)
        {
            var pointer = Path.Combine(directory, "latest");
            if (!File.Exists(pointer)) return null;
            id = Encoding.ASCII.GetString(await ReadBoundedAsync(pointer, 32, ct));
        }
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
            throw new ArgumentException("Invalid handoff ID.");
        var path = Path.Combine(directory, id + ".json");
        if (!File.Exists(path)) return null;
        var record = JsonSerializer.Deserialize<HandoffRecord>(await ReadBoundedAsync(path, MaxRecordBytes, ct), JsonOptions);
        if (record is null || record.Id != id || record.Scope != scope
            || string.IsNullOrWhiteSpace(record.Summary) || Encoding.UTF8.GetByteCount(record.Summary) > MaxSummaryBytes)
            throw new InvalidDataException("Handoff identity or content is invalid.");
        return record;
    }

    private string ScopeDirectory(HandoffScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.WorkspaceId) || string.IsNullOrWhiteSpace(scope.SessionId)
            || string.IsNullOrWhiteSpace(scope.AgentId)) throw new ArgumentException("A complete execution scope is required.");
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scope, JsonOptions)));
        return Path.Combine(_root, key);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > limit) throw new InvalidDataException("Handoff file exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, bool overwrite, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
