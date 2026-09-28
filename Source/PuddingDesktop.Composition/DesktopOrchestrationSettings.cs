using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Orchestration;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services.Orchestration;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-17 orchestration slice: graph discovery, draft validation and revision publishing through Core's
/// authoring service, plus the trigger view that backs the HTTP-hook card. Triggers are part of a revision, so
/// enabling or changing one publishes a new revision - there is no separate hook CRUD in Core.
/// </summary>
internal sealed class DesktopOrchestrationSettings(IDesktopKernel kernel) : IOrchestrationSettings
{
    private static readonly JsonSerializerOptions JsonOptions = AgentOrchestrationJson.CreateSerializerOptions();

    private Task<T> Orchestration<T>(string operationId,
        Func<IServiceProvider, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services, token), cancellationToken);

    public Task<IReadOnlyList<OrchestrationGraphSummary>> ListGraphsAsync(
        string workspaceId, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.graphs", async (services, token) =>
        {
            var store = services.GetRequiredService<IAgentOrchestrationQueryStore>();
            var graphs = await store.ListGraphsAsync(workspaceId, 200, 0, token);
            return (IReadOnlyList<OrchestrationGraphSummary>)graphs.Select(graph => new OrchestrationGraphSummary(
                graph.GraphId, graph.WorkspaceId, graph.RootSessionId, graph.CreatedByAgentId, graph.Objective,
                graph.CurrentRevision, graph.CurrentRevisionId, graph.RunCount, graph.ActiveRunCount,
                graph.CreatedAtUtc, graph.UpdatedAtUtc)).ToArray();
        }, cancellationToken);

    public Task<OrchestrationGraphDetail?> GetLatestAsync(string graphId, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.latest", async (services, token) =>
        {
            var store = services.GetRequiredService<IAgentOrchestrationQueryStore>();
            var definition = await store.GetLatestRevisionAsync(graphId, token);
            return definition is null ? null : Map(definition);
        }, cancellationToken);

    public Task<IReadOnlyList<OrchestrationRevisionSummary>> ListRevisionsAsync(
        string graphId, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.revisions", async (services, token) =>
        {
            var store = services.GetRequiredService<IAgentOrchestrationQueryStore>();
            var revisions = await store.ListRevisionsAsync(graphId, 50, token);
            return (IReadOnlyList<OrchestrationRevisionSummary>)revisions.Select(revision =>
                new OrchestrationRevisionSummary(revision.GraphId, revision.RevisionId, revision.Revision,
                    revision.ParentRevisionId ?? "", revision.SchemaVersion, revision.ContentHash,
                    revision.CreatedByAgentId, revision.CreatedAtUtc)).ToArray();
        }, cancellationToken);

    public Task<OrchestrationValidationResult> ValidateAsync(
        OrchestrationRevisionDraft draft, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.validate", async (services, token) =>
        {
            var definition = ParseOrThrow(draft.DefinitionJson);
            var result = await services.GetRequiredService<AgentOrchestrationAuthoringService>()
                .ValidateAsync(new AgentOrchestrationDraftValidateRequest
                {
                    GraphId = draft.GraphId.Trim(),
                    Definition = definition,
                }, token);
            return new OrchestrationValidationResult(
                result.IsValid,
                result.Issues.Select(issue => $"{issue.Code}: {issue.Message}").ToArray(),
                result.TopologicalNodeIds.ToArray());
        }, cancellationToken);

    public Task PublishAsync(OrchestrationRevisionDraft draft, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.publish", async (services, token) =>
        {
            var definition = ParseOrThrow(draft.DefinitionJson);
            var result = await services.GetRequiredService<AgentOrchestrationAuthoringService>()
                .CreateRevisionAsync(new AgentOrchestrationRevisionCreateRequest
                {
                    GraphId = draft.GraphId.Trim(),
                    ExpectedCurrentRevision = draft.ExpectedCurrentRevision,
                    Definition = definition,
                }, LocalDesktopIdentity.UserId, token);
            // 冲突与非法状态都来自 Core；冲突单独映射，让界面能说「已阻止覆盖」。
            if (!result.Success)
            {
                var reason = result.ErrorMessage ?? $"Core 以 {result.Status} 拒绝了该修订。";
                throw result.Status == AgentOrchestrationStoreStatus.Conflict
                    ? new SettingsConflictException($"编排修订冲突，已阻止覆盖：{reason}")
                    : new ArgumentException(reason, nameof(draft));
            }
            return true;
        }, cancellationToken);

    public Task<string> StartManualRunAsync(
        string graphId, string revisionId, CancellationToken cancellationToken = default)
        => Orchestration("orchestration.run", async (services, token) =>
        {
            var result = await services.GetRequiredService<AgentOrchestrationManualRunService>()
                .StartAsync(new AgentOrchestrationManualRunRequest
                {
                    GraphId = graphId.Trim(),
                    RevisionId = revisionId.Trim(),
                    RequestId = Guid.NewGuid().ToString("N"),
                    Inputs = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal),
                }, LocalDesktopIdentity.UserId, token);
            // 结果里带 Kind/ErrorCode：失败必须说清楚，而不是返回一个空 RunId。
            if (result.Kind != AgentOrchestrationManualRunResultKind.Success || result.Receipt is null)
                throw new InvalidOperationException(
                    $"Core 拒绝手动运行：{result.ErrorCode ?? result.Kind.ToString()}" +
                    (string.IsNullOrWhiteSpace(result.ErrorMessage) ? "" : $"（{result.ErrorMessage}）"));
            return result.Receipt.Run.RunId;
        }, cancellationToken);

    /// <summary>A malformed draft is a form error, not a Core refusal.</summary>
    private static AgentOrchestrationGraphDefinition ParseOrThrow(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentOrchestrationGraphDefinition>(json, JsonOptions)
                ?? throw new ArgumentException("定义 JSON 反序列化为空。", nameof(json));
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"定义 JSON 无法解析：{exception.Message}", nameof(json));
        }
    }

    private static OrchestrationGraphDetail Map(AgentOrchestrationGraphDefinition definition) => new(
        definition.GraphId, definition.RevisionId, definition.Revision, definition.WorkspaceId,
        definition.Objective, definition.SchemaVersion, "",
        definition.MaxConcurrency, definition.RequiresExplicitActivation,
        definition.Nodes.Select(node => new OrchestrationNodeSummary(
            node.NodeId, node.Component.ComponentType, node.Component.Version, node.Title)).ToArray(),
        definition.Edges.Select(edge => new OrchestrationEdgeSummary(
            edge.FromNodeId, edge.ToNodeId, edge.Kind.ToString())).ToArray(),
        definition.Triggers.Select(trigger => new OrchestrationTriggerSummary(
            trigger.TriggerId, trigger.Trigger.TriggerType, trigger.Trigger.Version, trigger.Enabled,
            // 只带键名：配置值可能引用凭据，不回显。
            trigger.Configuration.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray(),
            trigger.InputBindings.Select(binding => $"{binding.SourcePath} → {binding.TargetInputId}").ToArray()))
            .ToArray(),
        definition.Inputs.Select(input => new OrchestrationGraphInput(
            input.InputId, input.Contract.DataType, input.RequiredAtActivation)).ToArray());
}
