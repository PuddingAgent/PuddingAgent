using System.Collections.Concurrent;
using PuddingCode.Platform;

namespace PuddingController.Services;

/// <summary>
/// 知识库服务（V1 内存实现）。
/// 持有 Workspace 级文档索引，提供文档入库、移除和文本检索能力。
/// V2 替换点：向量化嵌入 + 向量数据库（如 pgvector）。
/// </summary>
public sealed class KnowledgeBaseService
{
    // key: workspaceId → (documentId → doc)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, KnowledgeDocument>> _store = new();

    // ── 文档管理 ─────────────────────────────────────────

    /// <summary>向 Workspace 知识库索引一条文档。若 DocumentId 重复则覆盖。</summary>
    public KnowledgeDocument IndexDocument(KnowledgeDocument doc)
    {
        var bucket = _store.GetOrAdd(doc.WorkspaceId, _ => new());
        bucket[doc.DocumentId] = doc;
        return doc;
    }

    /// <summary>移除指定文档，返回是否存在并已移除。</summary>
    public bool RemoveDocument(string workspaceId, string documentId)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return false;
        return bucket.TryRemove(documentId, out _);
    }

    /// <summary>返回 Workspace 所有已索引的文档。</summary>
    public IReadOnlyList<KnowledgeDocument> ListDocuments(string workspaceId)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return [];
        return bucket.Values.OrderByDescending(d => d.IndexedAt).ToList();
    }

    /// <summary>取单条文档；不存在则返回 null。</summary>
    public KnowledgeDocument? GetDocument(string workspaceId, string documentId)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket)) return null;
        bucket.TryGetValue(documentId, out var doc);
        return doc;
    }

    // ── 文本检索（V1：BM25 近似——关键词 TF 计分）────────

    /// <summary>
    /// 在 Workspace 知识库中按关键词搜索，返回相关文档列表（按分数降序）。
    /// V1 使用词频打分；V2 替换为向量召回。
    /// </summary>
    public IReadOnlyList<KnowledgeSearchResult> Search(string workspaceId, string query, int topK = 5)
    {
        if (!_store.TryGetValue(workspaceId, out var bucket) || bucket.IsEmpty) return [];

        var terms = Tokenize(query);
        if (terms.Length == 0) return [];

        var scored = bucket.Values
            .Select(doc => (doc, score: TermFrequencyScore(doc, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => new KnowledgeSearchResult
            {
                DocumentId = x.doc.DocumentId,
                Content = x.doc.Content.Length > 500 ? x.doc.Content[..500] + "…" : x.doc.Content,
                Title = x.doc.Title,
                Score = x.score
            })
            .ToList();

        return scored;
    }

    // ── 私有工具 ─────────────────────────────────────────

    private static string[] Tokenize(string text) =>
        text.ToLowerInvariant()
            .Split([' ', '\t', '\n', '\r', '.', ',', '!', '?', '；', '。', '，', '！', '？'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToArray();

    private static double TermFrequencyScore(KnowledgeDocument doc, string[] terms)
    {
        var text = (doc.Title + " " + doc.Content).ToLowerInvariant();
        double score = 0;
        foreach (var term in terms)
        {
            int idx = 0, count = 0;
            while ((idx = text.IndexOf(term, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += term.Length;
            }
            if (count > 0)
                score += 1.0 + Math.Log(count); // TF 近似
        }
        // Title 命中加权
        var titleLower = (doc.Title ?? "").ToLowerInvariant();
        foreach (var term in terms)
            if (titleLower.Contains(term, StringComparison.Ordinal))
                score += 2.0;
        return score;
    }
}
