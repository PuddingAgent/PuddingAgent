using System.Text.Json;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;

namespace PuddingPlatform.Services;

/// <summary>
/// JSON 内存头像目录（ADR-034 revised）。
/// 启动时从 Config/agent-avatars.json 加载，PNG 由 wwwroot 静态资源提供。
/// 不依赖数据库、播种或运行时文件复制。
/// </summary>
public sealed class AgentAvatarCatalog : IAgentAvatarCatalog
{
    private readonly IReadOnlyDictionary<string, AgentAvatarDefinition> _byId;
    private readonly IReadOnlyList<AgentAvatarDefinition> _sorted;
    private readonly AgentAvatarDefinition _default;

    public AgentAvatarCatalog(ILogger<AgentAvatarCatalog> logger)
    {
        var jsonPath = Path.Combine(AppContext.BaseDirectory, "Config", "agent-avatars.json");

        if (!File.Exists(jsonPath))
        {
            throw new InvalidOperationException(
                $"[AgentAvatarCatalog] agent-avatars.json not found at: {jsonPath}");
        }

        var json = File.ReadAllText(jsonPath);
        CatalogJson catalog;
        try
        {
            catalog = JsonSerializer.Deserialize<CatalogJson>(json, JsonOpts)
                       ?? throw new InvalidOperationException("Deserialization returned null.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"[AgentAvatarCatalog] Failed to parse {jsonPath}: {ex.Message}", ex);
        }

        if (catalog.Avatars is null || catalog.Avatars.Count == 0)
        {
            throw new InvalidOperationException(
                "[AgentAvatarCatalog] agent-avatars.json contains no avatars.");
        }

        var defaultId = catalog.DefaultAvatarId?.Trim();
        if (string.IsNullOrWhiteSpace(defaultId))
        {
            throw new InvalidOperationException(
                "[AgentAvatarCatalog] defaultAvatarId is missing or empty.");
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defs = new List<AgentAvatarDefinition>(catalog.Avatars.Count);

        foreach (var entry in catalog.Avatars)
        {
            var id = entry.AvatarId?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("[AgentAvatarCatalog] Avatar entry missing avatarId.");

            if (!ids.Add(id))
                throw new InvalidOperationException(
                    $"[AgentAvatarCatalog] Duplicate avatarId: {id}");

            var fileName = entry.FileName?.Trim();
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidOperationException(
                    $"[AgentAvatarCatalog] Avatar '{id}' missing fileName.");

            // 路径穿越防护
            if (fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
                throw new InvalidOperationException(
                    $"[AgentAvatarCatalog] Avatar '{id}' fileName contains path traversal: {fileName}");

            var sortOrder = entry.SortOrder > 0 ? entry.SortOrder : int.MaxValue;
            var name = entry.Name?.Trim() ?? id;
            var isEnabled = entry.IsEnabled;

            defs.Add(new AgentAvatarDefinition(
                AvatarId: id,
                Name: name,
                FileName: fileName,
                UrlPath: "/assets/agent-avatars/" + fileName,
                Personality: entry.Personality?.Trim(),
                RecommendedUse: entry.RecommendedUse?.Trim(),
                SortOrder: sortOrder,
                IsEnabled: isEnabled
            ));
        }

        var dict = defs.ToDictionary(d => d.AvatarId, StringComparer.OrdinalIgnoreCase);
        if (!dict.TryGetValue(defaultId, out var defaultDef))
        {
            throw new InvalidOperationException(
                $"[AgentAvatarCatalog] defaultAvatarId '{defaultId}' not found in avatars list.");
        }

        if (!defs.Any(d => d.IsEnabled))
        {
            throw new InvalidOperationException(
                "[AgentAvatarCatalog] No enabled avatars in catalog.");
        }

        _default = defaultDef;
        _byId = dict;
        _sorted = defs.OrderBy(d => d.SortOrder).ThenBy(d => d.AvatarId).ToList().AsReadOnly();

        logger.LogInformation(
            "[AgentAvatarCatalog] Loaded {Count} avatars (enabled={Enabled}), default={DefaultId}, version={Version}",
            _sorted.Count, _sorted.Count(d => d.IsEnabled), _default.AvatarId, catalog.Version);
    }

    // ── IAgentAvatarCatalog ────────────────────────────────

    public IReadOnlyList<AgentAvatarDefinition> List() => _sorted;

    public AgentAvatarDefinition GetDefault() => _default;

    public AgentAvatarDefinition? Find(string avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId)) return null;
        return _byId.TryGetValue(avatarId, out var def) ? def : null;
    }

    public string? ResolveUrl(string? avatarId)
    {
        if (string.IsNullOrWhiteSpace(avatarId)) return null;
        return _byId.TryGetValue(avatarId, out var def) ? def.UrlPath : null;
    }

    // ── JSON model ────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class CatalogJson
    {
        public int Version { get; set; }
        public string? DefaultAvatarId { get; set; }
        public List<JsonEntry>? Avatars { get; set; }
    }

    private sealed class JsonEntry
    {
        public string? AvatarId { get; set; }
        public string? Name { get; set; }
        public string? FileName { get; set; }
        public string? Personality { get; set; }
        public string? RecommendedUse { get; set; }
        public int SortOrder { get; set; }
        public bool IsEnabled { get; set; } = true;
    }
}
