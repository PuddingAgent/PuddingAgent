using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Agent 模板/用户画像读取服务（混合模式：优先文件，回退 DB）。
/// - 提供模板个性字段（全局模板 + 工作区覆盖）；
/// - 提供工作区用户画像文本。
/// Runtime 层只依赖抽象接口，不直接依赖 PlatformDbContext。
/// ADR-036：配置类数据唯一来源已迁移至文件，DB 表逐步废弃。
/// </summary>
public sealed class AgentTemplateProvider(
    IDbContextFactory<PlatformDbContext> dbFactory,
    AgentProfileProvider profileProvider,
    ILogger<AgentTemplateProvider> logger) : IAgentTemplateProvider, IWorkspaceProfileProvider
{
    public async Task<AgentTemplatePersona?> GetPersonaAsync(
        string templateId,
        string? workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        var canonicalId = NormalizeTemplateId(templateId);
        if (string.IsNullOrWhiteSpace(canonicalId))
            return null;

        // ADR-036: 尝试从文件模板加载（优先路径）
        var manifestPath = profileProvider.GetTemplateManifestPath(canonicalId);
        if (File.Exists(manifestPath))
        {
            try
            {
                var templateManifest = await profileProvider.LoadTemplateManifestAsync(canonicalId, ct);
                if (templateManifest is not null)
                {
                    var markdown = profileProvider.GetMarkdown(canonicalId);
                    return new AgentTemplatePersona
                    {
                        DisplayName = templateManifest.Name,
                        PersonaPrompt = markdown.Soul,
                        ToolsDescription = markdown.Tools,
                        BootstrapTemplate = markdown.Bootstrap,
                        MemorySearchMode = templateManifest.MemorySearchMode,
                    };
                }
            }
            catch
            {
                // 文件加载失败，回退 DB
            }
        }

        // 回退：DB 读取（兼容旧数据）
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var global = await db.GlobalAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.TemplateId == canonicalId && t.IsEnabled, ct);
        if (global is null)
            return null;

        var persona = new AgentTemplatePersona
        {
            DisplayName = global.Name,
            PersonaPrompt = global.PersonaPrompt,
            ToolsDescription = global.ToolsDescription,
            BootstrapTemplate = global.BootstrapTemplate,
            AvatarEmoji = global.AvatarEmoji,
            MemorySearchMode = global.MemorySearchMode,
            MemoryLlmProviderId = global.MemoryLlmProviderId,
            MemoryLlmModelId = global.MemoryLlmModelId,
        };

        if (string.IsNullOrWhiteSpace(workspaceId))
            return persona;

        var wsTemplate = await db.WorkspaceAgentTemplates
            .AsNoTracking()
            .FirstOrDefaultAsync(
                t => t.WorkspaceId == workspaceId && t.TemplateId == canonicalId && t.IsEnabled,
                ct);

        if (wsTemplate is null)
            return persona;

        return persona with
        {
            DisplayName = wsTemplate.Name ?? persona.DisplayName,
            PersonaPrompt = wsTemplate.PersonaPrompt ?? persona.PersonaPrompt,
            ToolsDescription = wsTemplate.ToolsDescription ?? persona.ToolsDescription,
            BootstrapTemplate = wsTemplate.BootstrapTemplate ?? persona.BootstrapTemplate,
            AvatarEmoji = wsTemplate.AvatarEmoji ?? persona.AvatarEmoji,
            MemorySearchMode = wsTemplate.MemorySearchMode ?? persona.MemorySearchMode,
            MemoryLlmProviderId = wsTemplate.MemoryLlmProviderId ?? persona.MemoryLlmProviderId,
            MemoryLlmModelId = wsTemplate.MemoryLlmModelId ?? persona.MemoryLlmModelId,
        };
    }

    public async Task<string?> GetWorkspaceUserProfileAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            return null;

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Workspaces
                .AsNoTracking()
                .Where(w => w.WorkspaceId == workspaceId)
                .Select(w => w.UserProfile)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[AgentTemplateProvider] Load workspace user profile failed workspace={Workspace}",
                workspaceId);
            return null;
        }
    }

    private static string NormalizeTemplateId(string templateId)
    {
        const string globalPrefix = "global:";
        return templateId.StartsWith(globalPrefix, StringComparison.OrdinalIgnoreCase)
            ? templateId[globalPrefix.Length..]
            : templateId;
    }
}
