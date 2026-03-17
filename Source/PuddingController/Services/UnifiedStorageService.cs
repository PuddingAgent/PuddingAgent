using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 统一存储服务（V1 内存实现）。
/// 提供 Workspace 级对象存取：Put / Get / Delete / List。
/// V2 替换点：对接 MinIO / S3 / NFS；接口不变，仅替换底层实现。
/// </summary>
public sealed class UnifiedStorageService
{
    private sealed record StoredObject(StorageObjectMeta Meta, byte[] Data);

    // key: workspaceId → (objectId → object)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StoredObject>> _store = new();

    // ── 写入 ─────────────────────────────────────────────

    /// <summary>
    /// 上传对象（base64 内容）。若同路径对象已存在，则覆盖。
    /// 返回对象元数据。
    /// </summary>
    public StorageObjectMeta Put(string workspaceId, StoragePutRequest req)
    {
        var data = Convert.FromBase64String(req.ContentBase64);
        var objectId = ComputeObjectId(workspaceId, req.Path);
        var meta = new StorageObjectMeta
        {
            ObjectId = objectId,
            WorkspaceId = workspaceId,
            Path = req.Path,
            SizeBytes = data.Length,
            ContentType = req.ContentType,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var bucket = _store.GetOrAdd(workspaceId, _ => new());
        bucket[objectId] = new StoredObject(meta, data);
        return meta;
    }

    // ── 读取 ─────────────────────────────────────────────

    /// <summary>按 objectId 取对象内容，不存在返回 null。</summary>
    public StorageGetResponse? Get(string workspaceId, string objectId)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return null;
        if (!bucket.TryGetValue(objectId, out var obj)) return null;
        return new StorageGetResponse
        {
            ObjectId = objectId,
            Path = obj.Meta.Path,
            ContentBase64 = Convert.ToBase64String(obj.Data),
            ContentType = obj.Meta.ContentType
        };
    }

    // ── 删除 ─────────────────────────────────────────────

    public bool Delete(string workspaceId, string objectId)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return false;
        return bucket.TryRemove(objectId, out _);
    }

    // ── 列举 ─────────────────────────────────────────────

    /// <summary>列举 Workspace 内所有对象元数据，可按路径前缀过滤。</summary>
    public IReadOnlyList<StorageObjectMeta> List(string workspaceId, string? pathPrefix = null)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return [];
        var query = bucket.Values.Select(o => o.Meta);
        if (!string.IsNullOrEmpty(pathPrefix))
            query = query.Where(m => m.Path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase));
        return query.OrderBy(m => m.Path).ToList();
    }

    // ── 工具 ─────────────────────────────────────────────

    private static string ComputeObjectId(string workspaceId, string path)
    {
        // 确定性 ID：workspace + path 的哈希，便于幂等 Put
        var raw = $"{workspaceId}::{path}";
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
