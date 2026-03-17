using System.Net.Http.Json;
using PuddingCode.Platform;

namespace PuddingRuntime.Services;

/// <summary>
/// Runtime 侧知识基础设施透明访问桥接。
/// Agent 通过此服务访问 Controller 托管的知识库、统一存储和知识图谱，
/// 不直接感知底层数据库或存储协议。
/// </summary>
public sealed class KnowledgeAccessRuntime
{
    private readonly HttpClient _http;

    public KnowledgeAccessRuntime(HttpClient http) => _http = http;

    // ── 知识库 ────────────────────────────────────────────

    /// <summary>在 Workspace 知识库内搜索文档（文本检索）。</summary>
    public async Task<IReadOnlyList<KnowledgeSearchResult>> SearchKnowledgeAsync(
        string workspaceId, string query, int topK = 5, CancellationToken ct = default)
    {
        var req = new { Query = query, TopK = topK };
        var resp = await _http.PostAsJsonAsync(
            $"/api/knowledge/{Uri.EscapeDataString(workspaceId)}/search", req, ct);
        if (!resp.IsSuccessStatusCode) return [];
        var results = await resp.Content.ReadFromJsonAsync<List<KnowledgeSearchResult>>(ct);
        return results ?? [];
    }

    /// <summary>向 Workspace 知识库索引一条文档。</summary>
    public async Task<KnowledgeDocument?> IndexDocumentAsync(
        KnowledgeDocument doc, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/knowledge/{Uri.EscapeDataString(doc.WorkspaceId)}/documents", doc, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<KnowledgeDocument>(ct);
    }

    /// <summary>列举 Workspace 已索引文档。</summary>
    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(
        string workspaceId, CancellationToken ct = default)
    {
        var list = await _http.GetFromJsonAsync<List<KnowledgeDocument>>(
            $"/api/knowledge/{Uri.EscapeDataString(workspaceId)}/documents", ct);
        return list ?? [];
    }

    // ── 统一存储 ──────────────────────────────────────────

    /// <summary>上传对象到 Workspace 存储（内容为 base64 字符串）。</summary>
    public async Task<StorageObjectMeta?> PutObjectAsync(
        string workspaceId, string path, byte[] content,
        string? contentType = null, CancellationToken ct = default)
    {
        var req = new StoragePutRequest
        {
            Path = path,
            ContentBase64 = Convert.ToBase64String(content),
            ContentType = contentType
        };
        var resp = await _http.PutAsJsonAsync(
            $"/api/storage/{Uri.EscapeDataString(workspaceId)}/objects", req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<StorageObjectMeta>(ct);
    }

    /// <summary>从 Workspace 存储下载对象，返回原始字节。</summary>
    public async Task<(byte[]? content, string? contentType)> GetObjectAsync(
        string workspaceId, string objectId, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(
            $"/api/storage/{Uri.EscapeDataString(workspaceId)}/objects/{Uri.EscapeDataString(objectId)}", ct);
        if (!resp.IsSuccessStatusCode) return (null, null);
        var obj = await resp.Content.ReadFromJsonAsync<StorageGetResponse>(ct);
        if (obj is null) return (null, null);
        return (Convert.FromBase64String(obj.ContentBase64), obj.ContentType);
    }

    /// <summary>列举 Workspace 对象元数据，可按路径前缀过滤。</summary>
    public async Task<IReadOnlyList<StorageObjectMeta>> ListObjectsAsync(
        string workspaceId, string? pathPrefix = null, CancellationToken ct = default)
    {
        var url = $"/api/storage/{Uri.EscapeDataString(workspaceId)}/objects";
        if (!string.IsNullOrEmpty(pathPrefix))
            url += $"?prefix={Uri.EscapeDataString(pathPrefix)}";
        var list = await _http.GetFromJsonAsync<List<StorageObjectMeta>>(url, ct);
        return list ?? [];
    }

    // ── 知识图谱 ──────────────────────────────────────────

    /// <summary>查询 Workspace 图谱实体。</summary>
    public async Task<IReadOnlyList<GraphEntity>> QueryEntitiesAsync(
        string workspaceId, GraphQueryRequest req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(
            $"/api/graph/{Uri.EscapeDataString(workspaceId)}/entities/query", req, ct);
        if (!resp.IsSuccessStatusCode) return [];
        var results = await resp.Content.ReadFromJsonAsync<List<GraphEntity>>(ct);
        return results ?? [];
    }

    /// <summary>添加或更新图谱实体。</summary>
    public async Task<GraphEntity?> UpsertEntityAsync(
        GraphEntity entity, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync(
            $"/api/graph/{Uri.EscapeDataString(entity.WorkspaceId)}/entities", entity, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<GraphEntity>(ct);
    }

    /// <summary>获取 Workspace 图谱关系，可按 entityId 过滤。</summary>
    public async Task<IReadOnlyList<GraphRelation>> GetRelationsAsync(
        string workspaceId, string? entityId = null, CancellationToken ct = default)
    {
        var url = $"/api/graph/{Uri.EscapeDataString(workspaceId)}/relations";
        if (!string.IsNullOrEmpty(entityId))
            url += $"?entityId={Uri.EscapeDataString(entityId)}";
        var list = await _http.GetFromJsonAsync<List<GraphRelation>>(url, ct);
        return list ?? [];
    }

    /// <summary>添加或更新图谱关系。</summary>
    public async Task<GraphRelation?> UpsertRelationAsync(
        GraphRelation relation, CancellationToken ct = default)
    {
        var resp = await _http.PutAsJsonAsync(
            $"/api/graph/{Uri.EscapeDataString(relation.WorkspaceId)}/relations", relation, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<GraphRelation>(ct);
    }
}
