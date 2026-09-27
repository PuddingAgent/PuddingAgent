using PuddingDesktop.Foundation;
using PuddingPlatform.Data.Dtos;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-04 guardrail slice: max rounds, max elapsed seconds, max total tool calls and the container image
/// override, for the global template and for a workspace role instance.
///
/// Core's own defaults are 200 / 86400 / 100 and an inherited container image; the form never invents a
/// different default, it only refuses values (≤ 0) that no budget could honour.
/// </summary>
internal sealed partial class DesktopAgentDirectorySettings
{
    public Task<AgentGuardrailPolicy> ReadTemplateGuardrailsAsync(string templateId, CancellationToken cancellationToken = default)
        => Templates("agents.guardrails.template.read", async (service, token) =>
        {
            var template = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            return Map(template.MaxRounds, template.MaxElapsedSeconds, template.MaxToolCallsTotal, template.ContainerImage);
        }, cancellationToken);

    public Task SaveTemplateGuardrailsAsync(string templateId, AgentGuardrailPolicy policy, CancellationToken cancellationToken = default)
        => Templates("agents.guardrails.template.save", async (service, token) =>
        {
            Guard(policy);
            var current = await service.GetTemplateAsync(templateId, token)
                ?? throw new InvalidOperationException($"模板 {templateId} 不存在。");
            await service.UpdateTemplateAsync(templateId, TemplateRequest(current,
                maxRounds: policy.MaxRounds,
                maxElapsedSeconds: policy.MaxElapsedSeconds,
                maxToolCallsTotal: policy.MaxToolCallsTotal,
                containerImage: AgentGuardrailText.NormalizeContainerImage(policy.ContainerImage)), token);
            return true;
        }, cancellationToken);

    public Task<AgentGuardrailPolicy> ReadInstanceGuardrailsAsync(string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Instances("agents.guardrails.instance.read", async (service, token) =>
        {
            var agent = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            return Map(agent.MaxRounds, agent.MaxElapsedSeconds, agent.MaxToolCallsTotal, agent.ContainerImage);
        }, cancellationToken);

    public Task SaveInstanceGuardrailsAsync(string workspaceId, string agentId, AgentGuardrailPolicy policy, CancellationToken cancellationToken = default)
        => Instances("agents.guardrails.instance.save", async (service, token) =>
        {
            Guard(policy);
            var current = await service.GetAgentAsync(workspaceId, agentId, token)
                ?? throw new InvalidOperationException($"角色 {agentId} 不存在。");
            await service.UpdateAgentProfileAsync(workspaceId, agentId, InstanceRequest(current,
                maxRounds: policy.MaxRounds,
                maxElapsedSeconds: policy.MaxElapsedSeconds,
                maxToolCallsTotal: policy.MaxToolCallsTotal,
                containerImage: AgentGuardrailText.NormalizeContainerImage(policy.ContainerImage)), token);
            return true;
        }, cancellationToken);

    private static AgentGuardrailPolicy Map(int maxRounds, int maxElapsedSeconds, int maxToolCallsTotal, string? containerImage) =>
        new(maxRounds, maxElapsedSeconds, maxToolCallsTotal, containerImage ?? "");

    private static void Guard(AgentGuardrailPolicy policy)
    {
        var errors = AgentGuardrailText.Validate(policy);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
    }


}
