using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Orchestration;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingRuntime.Services.Orchestration;

/// <summary>
/// Executes a frozen Sub-agent component through the existing invocation facade. The orchestration
/// runtime owns only port mapping and durable identities; Agent lifecycle, budgets, run archives,
/// model protocols, and session management remain owned by ISubAgentInvocationService.
/// </summary>
public sealed class SubAgentOrchestrationNodeExecutor(
    ISubAgentInvocationService subAgents,
    ILlmConfigService llmConfigs) : IAgentOrchestrationNodeExecutor
{
    public bool CanExecute(AgentOrchestrationNodeDefinition node)
        => string.Equals(
               node.Component.ComponentType,
               AgentOrchestrationComponentTypes.SubAgent,
               StringComparison.OrdinalIgnoreCase)
           && node.Executor?.Kind == AgentOrchestrationExecutorKind.SubAgent;

    public async Task<AgentOrchestrationNodeExecutionResult> ExecuteAsync(
        AgentOrchestrationNodeExecutionContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!CanExecute(context.Node))
            throw new InvalidOperationException($"Node '{context.Node.NodeId}' is not a Sub-agent component.");
        if (context.Node.PermissionMode != AgentOrchestrationPermissionMode.ReadOnly)
        {
            throw new InvalidOperationException(
                $"Sub-agent node '{context.Node.NodeId}' requires an approval-aware executor for write permissions.");
        }

        var executor = context.Node.Executor!;
        var (providerId, modelId) = ParseExactRoute(executor.RouteKey);
        var llmConfig = llmConfigs.Resolve(providerId, modelId)
            ?? throw new InvalidOperationException(
                $"Exact Sub-agent route '{executor.RouteKey}' is not configured or enabled.");
        var templateId = RequireText(executor.TemplateId, "TemplateId");
        var role = RequireText(executor.Role, "Role");
        var request = AgentOrchestrationNodeInputResolver.ResolveInlineText(context, "request");
        if (string.IsNullOrWhiteSpace(request))
        {
            throw new InvalidOperationException(
                $"Sub-agent node '{context.Node.NodeId}' received no inline request.");
        }

        var supplementalContext = AgentOrchestrationNodeInputResolver.ResolveInlineText(context, "context");
        var result = await subAgents.InvokeAsync(new SubAgentInvocationRequest
        {
            ParentSessionId = context.Run.RootSessionId,
            WorkspaceId = context.Run.WorkspaceId,
            // RequestedByAgentId is an audit principal (for example "manual:admin" or
            // "http-hook:trigger:admin"), not a filesystem-safe persistent Agent instance id.
            // Sub-agent archives use this field as a directory segment, so orchestration owns a
            // stable execution identity derived only from immutable graph facts.
            ParentAgentInstanceId = CreateExecutionOwnerId(context),
            ParentAgentId = context.Run.RequestedByAgentId,
            TemplateId = templateId,
            Task = BuildTask(context.Node.Objective, request),
            DelegationProtocol = "pudding-agent-orchestration/v1",
            IsAsync = false,
            LlmConfig = llmConfig,
            LlmProfile = new LlmInvocationProfile
            {
                ProviderId = providerId,
                ProfileId = $"orchestration.{ToProfileToken(role)}",
                ModelId = modelId,
                Role = role
            },
            ParentContextSnapshot = string.IsNullOrWhiteSpace(supplementalContext)
                ? null
                : supplementalContext,
            CapabilityPolicy = BuildTextPlanningCapabilityPolicy(),
            TaskPlanId = context.Run.RunId,
            TaskNodeId = context.Node.NodeId,
            RoleInPlan = role,
            AllowSubDelegation = false,
            AllowAgentCreation = false,
            AssignedObjective = context.Node.Objective,
            ExpectedOutputContract = context.Node.ExpectedOutputContract,
            PermissionMode = SubAgentPermissionModes.None,
            TimeoutSeconds = context.Node.TimeoutSeconds,
            InvocationId = context.Claim.ClaimId,
            OriginToolId = "agent_orchestration"
        }, ct);

        if (!IsSuccessful(result.Status) || string.IsNullOrWhiteSpace(result.Reply))
        {
            throw new InvalidOperationException(
                result.Error ??
                $"Sub-agent '{context.Node.NodeId}' ended with status '{result.Status}' and no usable output.");
        }

        var reply = result.Reply.Trim();
        return new AgentOrchestrationNodeExecutionResult
        {
            Summary = BuildSummary(reply),
            ExecutionRunId = result.RunId,
            SubSessionId = result.SubSessionId,
            Outputs = new Dictionary<string, AgentOrchestrationValueEnvelope>(StringComparer.Ordinal)
            {
                ["result"] = new()
                {
                    DataType = AgentOrchestrationDataTypes.Content,
                    ContentType = "text/markdown",
                    InlineValue = JsonSerializer.SerializeToElement(reply)
                }
            }
        };
    }

    private static CapabilityPolicy BuildTextPlanningCapabilityPolicy() => new()
    {
        AllowShellExecution = false,
        AllowFileWrite = false,
        AllowNetworkAccess = false,
        AllowedToolNames = Array.Empty<string>(),
        DefaultToolNames = Array.Empty<string>(),
        RequiresGrantToolNames = Array.Empty<string>()
    };

    private static string CreateExecutionOwnerId(AgentOrchestrationNodeExecutionContext context)
    {
        var material = $"{context.Run.WorkspaceId}\n{context.Definition.GraphId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"orchestration-{Convert.ToHexString(hash).ToLowerInvariant()[..24]}";
    }

    private static string BuildTask(string objective, string request)
        => $"Objective:\n{objective.Trim()}\n\nInput:\n{request.Trim()}";

    private static string BuildSummary(string reply)
    {
        const int maxLength = 180;
        var singleLine = string.Join(' ', reply.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..maxLength]}…";
    }

    private static bool IsSuccessful(string status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase);

    private static (string ProviderId, string ModelId) ParseExactRoute(string? routeKey)
    {
        var route = RequireText(routeKey, "RouteKey");
        var separator = route.IndexOf('/');
        if (separator <= 0 || separator == route.Length - 1)
        {
            throw new InvalidOperationException(
                $"Sub-agent route '{route}' must use the exact 'provider/model' format.");
        }

        return (route[..separator].Trim(), route[(separator + 1)..].Trim());
    }

    private static string RequireText(string? value, string name)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Sub-agent executor {name} is required.")
            : value.Trim();

    private static string ToProfileToken(string role)
    {
        var token = new string(role.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        return token.Length == 0 ? "agent" : token;
    }
}
