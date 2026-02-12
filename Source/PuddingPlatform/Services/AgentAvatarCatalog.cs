using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services;

/// <summary>
/// 头像目录查询服务。提供启用头像列表、按 ID 查询、默认头像解析等能力。
/// 供 Controller 和种子服务使用。
/// </summary>
public class AgentAvatarCatalog(
    IDbContextFactory<PlatformDbContext> dbFactory,
    ILogger<AgentAvatarCatalog> logger)
{
    /// <summary>获取所有启用头像，按 SortOrder 排序</summary>
    public async Task<List<AgentAvatarEntity>> ListEnabledAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var fromDb = await db.AgentAvatars
            .AsNoTracking()
            .Where(a => a.IsEnabled)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Id)
            .ToListAsync();

        return fromDb.Count > 0 ? fromDb : [HardcodedDefault];
    }

    /// <summary>
    /// 按 AvatarId 查找头像，要求启用状态。
    /// 不存在或已禁用时返回 null。
    /// </summary>
    public async Task<AgentAvatarEntity?> GetRequiredEnabledAsync(string avatarId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AgentAvatars
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AvatarId == avatarId && a.IsEnabled);
    }

    /// <summary>获取系统默认头像：SortOrder 最小且 IsEnabled 的第一个。DB 无记录时返回硬编码默认。</summary>
    public async Task<AgentAvatarEntity?> GetDefaultAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var fromDb = await db.AgentAvatars
            .AsNoTracking()
            .Where(a => a.IsEnabled)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync();

        if (fromDb is not null)
            return fromDb;

        // 数据库无头像记录时，返回硬编码默认头像
        return HardcodedDefault;
    }

    /// <summary>硬编码默认头像。数据库无种子数据时兜底，避免启动报错。</summary>
    private static AgentAvatarEntity HardcodedDefault { get; } = new()
    {
        AvatarId = "default",
        Name = "Default",
        FileName = "agent-avatar-default.png",
        UrlPath = "/assets/agent-avatars/agent-avatar-default.png",
        IsEnabled = true,
        SortOrder = 99,
    };

    /// <summary>
    /// 解析头像 URL。优先级：
    /// avatarId（非空且启用）> legacyUrl > null。
    /// 都不存在时返回 null（由调用方决定 legacyEmoji fallback）。
    /// </summary>
    public async Task<string?> ResolveUrlAsync(string? avatarId, string? legacyUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(avatarId))
        {
            var avatar = await GetRequiredEnabledAsync(avatarId);
            if (avatar is not null)
                return avatar.UrlPath;
        }
        return legacyUrl;
    }

    /// <summary>
    /// 获取启用头像的 <see cref="AgentAvatarEntity"/> 字典（AvatarId → Entity），
    /// 用于批量解析，避免 N+1。
    /// </summary>
    public async Task<Dictionary<string, AgentAvatarEntity>> GetEnabledMapAsync()
    {
        var list = await ListEnabledAsync();
        return list.ToDictionary(a => a.AvatarId, a => a);
    }
}
