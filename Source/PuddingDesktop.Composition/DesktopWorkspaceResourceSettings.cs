using Microsoft.Extensions.DependencyInjection;
using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-05 resource slice: binds the knowledge / skill / workflow cards to the shared
/// WorkspaceResourceService. Workspace isolation and MCP config validation stay in Core.
/// </summary>
internal sealed class DesktopWorkspaceResourceSettings(IDesktopKernel kernel) : IWorkspaceResourceSettings
{
    private Task<T> Resources<T>(string operationId,
        Func<WorkspaceResourceService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<WorkspaceResourceService>(), token), cancellationToken);

    // ── 知识库 ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<KnowledgeBaseSummary>> ListKnowledgeBasesAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.knowledge.list", async (service, token) =>
        {
            var result = await service.ListKnowledgeBasesAsync(workspaceId, token);
            DesktopSkillHubSettings.Require(result);
            return (IReadOnlyList<KnowledgeBaseSummary>)result.Value!
                .Select(kb => new KnowledgeBaseSummary(kb.KbId, kb.Name, kb.Description ?? "", kb.KbType,
                    kb.DocumentCount, kb.IsEnabled, kb.CreatedAt, kb.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task SaveKnowledgeBaseAsync(KnowledgeBaseEdit edit, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.knowledge.save", async (service, token) =>
        {
            var draft = new KnowledgeBaseDraft(edit.Name.Trim(), Nullable(edit.Description), edit.KbType, edit.IsEnabled);
            var result = string.IsNullOrWhiteSpace(edit.KbId)
                ? await service.CreateKnowledgeBaseAsync(edit.WorkspaceId, draft, token)
                : await service.UpdateKnowledgeBaseAsync(edit.WorkspaceId, edit.KbId, draft, token);
            DesktopSkillHubSettings.Require(result);
            return true;
        }, cancellationToken);

    public Task DeleteKnowledgeBaseAsync(string workspaceId, string kbId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.knowledge.delete", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.DeleteKnowledgeBaseAsync(workspaceId, kbId, token));
            return true;
        }, cancellationToken);

    // ── 工作区技能 ────────────────────────────────────────────────────

    public Task<IReadOnlyList<WorkspaceSkillSummary>> ListSkillsAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.skills.list", async (service, token) =>
        {
            var result = await service.ListSkillsAsync(workspaceId, token);
            DesktopSkillHubSettings.Require(result);
            return (IReadOnlyList<WorkspaceSkillSummary>)result.Value!
                .Select(skill => new WorkspaceSkillSummary(skill.SkillId, skill.Name, skill.Description ?? "",
                    skill.SkillType, skill.ConfigJson ?? "", skill.IsEnabled, skill.CreatedAt, skill.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task SaveSkillAsync(WorkspaceSkillEdit edit, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.skills.save", async (service, token) =>
        {
            var draft = new WorkspaceSkillDraft(edit.Name.Trim(), Nullable(edit.Description), edit.SkillType,
                Nullable(edit.ConfigJson), edit.IsEnabled);
            var result = string.IsNullOrWhiteSpace(edit.SkillId)
                ? await service.CreateSkillAsync(edit.WorkspaceId, draft, token)
                : await service.UpdateSkillAsync(edit.WorkspaceId, edit.SkillId, draft, token);
            DesktopSkillHubSettings.Require(result);
            return true;
        }, cancellationToken);

    public Task DeleteSkillAsync(string workspaceId, string skillId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.skills.delete", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.DeleteSkillAsync(workspaceId, skillId, token));
            return true;
        }, cancellationToken);

    // ── 工作流 ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<WorkspaceWorkflowSummary>> ListWorkflowsAsync(string workspaceId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.workflows.list", async (service, token) =>
        {
            var result = await service.ListWorkflowsAsync(workspaceId, token);
            DesktopSkillHubSettings.Require(result);
            return (IReadOnlyList<WorkspaceWorkflowSummary>)result.Value!
                .Select(workflow => new WorkspaceWorkflowSummary(workflow.WorkflowId, workflow.Name,
                    workflow.Description ?? "", workflow.DefinitionJson ?? "", workflow.Status,
                    workflow.IsEnabled, workflow.CreatedAt, workflow.UpdatedAt)).ToArray();
        }, cancellationToken);

    public Task SaveWorkflowAsync(WorkspaceWorkflowEdit edit, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.workflows.save", async (service, token) =>
        {
            var draft = new WorkspaceWorkflowDraft(edit.Name.Trim(), Nullable(edit.Description),
                edit.DefinitionJson ?? "", edit.Status, edit.IsEnabled);
            var result = string.IsNullOrWhiteSpace(edit.WorkflowId)
                ? await service.CreateWorkflowAsync(edit.WorkspaceId, draft, token)
                : await service.UpdateWorkflowAsync(edit.WorkspaceId, edit.WorkflowId, draft, token);
            DesktopSkillHubSettings.Require(result);
            return true;
        }, cancellationToken);

    public Task DeleteWorkflowAsync(string workspaceId, string workflowId, CancellationToken cancellationToken = default)
        => Resources("workspaceResources.workflows.delete", async (service, token) =>
        {
            DesktopSkillHubSettings.Require(await service.DeleteWorkflowAsync(workspaceId, workflowId, token));
            return true;
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
