using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingPlatform.Data;

namespace PuddingPlatform.Services;

/// <summary>
/// Agent 模板读取（纯文件模式 — data/agent-templates/{id}/）。
/// A方案：配置文件为唯一来源，DB 不再参与模板解析。
/// Workspace user profile 保留 DB 读取（业务数据，非配置）。
/// </summary>
public sealed class AgentTemplateProvider(
    AgentProfileProvider profileProvider,
    IDbContextFactory<PlatformDbContext>? dbFactory,
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

        var manifestPath = profileProvider.GetTemplateManifestPath(canonicalId);
        if (!File.Exists(manifestPath))
            return null;

        try
        {
            var templateManifest = await profileProvider.LoadTemplateManifestAsync(canonicalId, ct);
            if (templateManifest is null) return null;

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
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "[AgentTemplateProvider] Failed to load template from file template={Template}", canonicalId);
            return null;
        }
    }

    public async Task<string?> GetWorkspaceUserProfileAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workspaceId) || dbFactory is null) return null;
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
            logger.LogWarning(ex,
                "[AgentTemplateProvider] Load workspace user profile failed workspace={Workspace}", workspaceId);
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
