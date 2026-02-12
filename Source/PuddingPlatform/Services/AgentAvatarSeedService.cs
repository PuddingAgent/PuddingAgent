using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 启动时从 wwwroot/assets/agent-avatars/avatars.json 读取预置头像，
/// 按 AvatarId 幂等地 upsert 到 AgentAvatars 表。
/// 不覆盖管理员手动禁用的 IsEnabled = false。
/// </summary>
public class AgentAvatarSeedService(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<AgentAvatarSeedService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task SeedAsync(string avatarsJsonDir)
    {
        var jsonPath = Path.Combine(avatarsJsonDir, "avatars.json");
        if (!File.Exists(jsonPath))
        {
            logger.LogWarning("[AgentAvatarSeed] avatars.json 不存在: {Path}", jsonPath);
            return;
        }

        List<AvatarJsonEntry>? entries;
        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            entries = JsonSerializer.Deserialize<List<AvatarJsonEntry>>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[AgentAvatarSeed] 解析 avatars.json 失败");
            return;
        }

        if (entries is null || entries.Count == 0)
        {
            logger.LogWarning("[AgentAvatarSeed] avatars.json 为空");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var now = DateTimeOffset.UtcNow;
        var sortOrder = 10;

        foreach (var entry in entries)
        {
            var avatarId = entry.Id;
            if (string.IsNullOrWhiteSpace(avatarId))
            {
                logger.LogWarning("[AgentAvatarSeed] 跳过无 id 的头像条目");
                continue;
            }

            var fileName = entry.File;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                logger.LogWarning("[AgentAvatarSeed] 头像 {AvatarId} 缺少 file 字段，跳过", avatarId);
                continue;
            }

            var urlPath = "/assets/agent-avatars/" + fileName;

            // 检查 PNG 文件是否存在
            var pngPath = Path.Combine(avatarsJsonDir, fileName);
            if (!File.Exists(pngPath))
            {
                logger.LogWarning("[AgentAvatarSeed] 头像图片不存在: {Path}（仍会写入元数据）", pngPath);
            }

            var visualTraitsJson = entry.VisualTraits is { Count: > 0 }
                ? JsonSerializer.Serialize(entry.VisualTraits)
                : "[]";

            // 幂等 upsert：不覆盖管理员手工禁用的 IsEnabled
            var existing = await db.AgentAvatars.FirstOrDefaultAsync(a => a.AvatarId == avatarId);
            if (existing is not null)
            {
                // 只更新变更字段，不重置 IsEnabled
                existing.Name = entry.Name ?? existing.Name;
                existing.FileName = fileName;
                existing.UrlPath = urlPath;
                existing.Personality = entry.Personality ?? existing.Personality;
                existing.HairColor = entry.HairColor ?? existing.HairColor;
                existing.Expression = entry.Expression ?? existing.Expression;
                existing.VisualTraitsJson = visualTraitsJson;
                existing.RecommendedUse = entry.RecommendedUse ?? existing.RecommendedUse;
                existing.SortOrder = sortOrder;
                existing.UpdatedAt = now;
                logger.LogDebug("[AgentAvatarSeed] 更新头像: {AvatarId}", avatarId);
            }
            else
            {
                db.AgentAvatars.Add(new AgentAvatarEntity
                {
                    AvatarId = avatarId,
                    Name = entry.Name ?? avatarId,
                    FileName = fileName,
                    UrlPath = urlPath,
                    Personality = entry.Personality,
                    HairColor = entry.HairColor,
                    Expression = entry.Expression,
                    VisualTraitsJson = visualTraitsJson,
                    RecommendedUse = entry.RecommendedUse,
                    IsBuiltIn = true,
                    IsEnabled = true,
                    SortOrder = sortOrder,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                logger.LogDebug("[AgentAvatarSeed] 新增头像: {AvatarId}", avatarId);
            }

            sortOrder += 10;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("[AgentAvatarSeed] 种子完成，共处理 {Count} 个头像", entries.Count);
    }

    // ── JSON 模型 ──────────────────────────────────────

    private class AvatarJsonEntry
    {
        public string Id { get; set; } = string.Empty;
        public string? File { get; set; }
        public string? Name { get; set; }
        public string? Personality { get; set; }
        public string? HairColor { get; set; }
        public string? Expression { get; set; }
        public List<string>? VisualTraits { get; set; }
        public string? RecommendedUse { get; set; }
    }
}
