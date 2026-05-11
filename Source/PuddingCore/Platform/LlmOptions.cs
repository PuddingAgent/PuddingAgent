using System.Collections.Concurrent;

namespace PuddingCode.Platform.Options
{
    /// <summary>LLM 连接配置（OpenAI-compatible）。</summary>
    public sealed record LlmOptions(
        string Endpoint,
        string ApiKey,
        string Model,
        double? Temperature = null,
        int? MaxTokens = null,
        string? ReasoningEffort = null,
        string? ThinkingMode = null);
}

namespace PuddingCode.Platform
{
    /// <summary>上下文组装快照存储（线程安全，供调试端点读取）。</summary>
    public sealed class ContextAssemblyStore
    {
        private readonly ConcurrentDictionary<string, ContextAssemblySnapshot> _snapshots = new();

        public void Set(ContextAssemblySnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot.SessionId))
                return;
            _snapshots[snapshot.SessionId] = snapshot;
        }

        public bool TryGet(string sessionId, out ContextAssemblySnapshot? snapshot)
        {
            var ok = _snapshots.TryGetValue(sessionId, out var found);
            snapshot = found;
            return ok;
        }
    }

    /// <summary>上下文组装诊断快照。</summary>
    public class ContextAssemblySnapshot
    {
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset AssembledAt { get; set; }
        public List<ContextLayerInfo> Layers { get; set; } = [];
        public int TotalTokens { get; set; }
    }

    /// <summary>单层上下文诊断信息。</summary>
    public class ContextLayerInfo
    {
        public string LayerName { get; set; } = string.Empty;
        public int TokenCount { get; set; }
        public string ContentPreview { get; set; } = string.Empty;
    }
}