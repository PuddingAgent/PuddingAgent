using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent 私有状态维护工具。
/// Agent 身份只来自 ToolExecutionContext；参数不能选择其他 Agent 或传入物理路径。
/// </summary>
[Tool(
    id: "agent_state",
    name: "Agent state",
    description:
        "Inspect, read, diagnose, and update this Agent's own configuration documents. " +
        "Supported documents: soul, agents, tools, bootstrap, memory, heartbeat. " +
        "The tool is always scoped to the current Agent instance and cannot access another Agent or arbitrary paths.",
    category: ToolCategory.FileSystem,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ConcurrencySafe,
    SortOrder = 44)]
public sealed class AgentStateTool(
    IServiceProvider serviceProvider,
    ILogger<AgentStateTool> logger) : PuddingToolBase<AgentStateArgs>
{
    private const int DefaultMaxReadChars = 100_000;
    private const int MaxReadChars = 200_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        AgentStateArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var action = NormalizeAction(args.Action);
        var agentInstanceId = context.AgentInstanceId;
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return ToolExecutionResult.Fail("Current Agent instance ID is unavailable; self-maintenance was rejected.");

        var service = serviceProvider.GetService<IAgentSelfMaintenanceService>();
        if (service is null)
        {
            return ToolExecutionResult.Fail(
                "Agent self-maintenance service is not registered in this runtime.");
        }

        try
        {
            return action switch
            {
                "inspect" or "diagnose" or "list" =>
                    ToolExecutionResult.Ok(ToJson(await InspectAsync(service, agentInstanceId, action, ct))),
                "read" =>
                    ToolExecutionResult.Ok(ToJson(await ReadAsync(service, agentInstanceId, args, ct))),
                "update" =>
                    ToolExecutionResult.Ok(ToJson(await UpdateAsync(service, agentInstanceId, args, ct))),
                _ => ToolExecutionResult.Fail(
                    $"Unknown agent_state action '{args.Action}'. Valid actions: inspect, diagnose, read, update."),
            };
        }
        catch (AgentSelfStateConflictException ex)
        {
            return ToolExecutionResult.Fail(ex.Message, exitCode: 2);
        }
        catch (ArgumentException ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
        catch (FileNotFoundException ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "[AgentState] action={Action} agent={Agent} session={Session}",
                action,
                agentInstanceId,
                context.SessionId);
            return ToolExecutionResult.Fail(
                $"Agent state operation '{action}' failed. Check runtime diagnostics for details.");
        }
    }

    private static async Task<object> InspectAsync(
        IAgentSelfMaintenanceService service,
        string agentInstanceId,
        string action,
        CancellationToken ct)
    {
        var snapshot = await service.InspectAsync(agentInstanceId, ct);
        return new
        {
            status = snapshot.IsHealthy ? "healthy" : "attention_required",
            action,
            snapshot.AgentInstanceId,
            snapshot.TemplateId,
            snapshot.DisplayName,
            snapshot.IsEnabled,
            snapshot.IsHealthy,
            snapshot.Documents,
            snapshot.Issues,
            supportedDocuments = AgentSelfStateDocuments.All,
        };
    }

    private static async Task<object> ReadAsync(
        IAgentSelfMaintenanceService service,
        string agentInstanceId,
        AgentStateArgs args,
        CancellationToken ct)
    {
        var document = RequireDocument(args);
        var state = await service.ReadDocumentAsync(agentInstanceId, document, ct);
        var maxChars = Math.Clamp(args.MaxChars ?? DefaultMaxReadChars, 1, MaxReadChars);
        var truncated = state.Content.Length > maxChars;
        var content = truncated ? state.Content[..maxChars] : state.Content;
        return new
        {
            status = "ok",
            action = "read",
            state.AgentInstanceId,
            state.Document,
            state.FileName,
            content,
            originalLength = state.Content.Length,
            truncated,
            state.Sha256,
            state.LastModifiedAt,
        };
    }

    private static async Task<object> UpdateAsync(
        IAgentSelfMaintenanceService service,
        string agentInstanceId,
        AgentStateArgs args,
        CancellationToken ct)
    {
        var document = RequireDocument(args);
        if (args.Content is null)
            throw new ArgumentException("content is required for agent_state action=update.", nameof(args));

        var result = await service.UpdateDocumentAsync(
            agentInstanceId,
            document,
            args.Content,
            args.ExpectedSha256,
            ct);
        return new
        {
            status = "updated",
            action = "update",
            result.AgentInstanceId,
            result.Document,
            result.FileName,
            result.PreviousSha256,
            result.Sha256,
            result.Length,
            result.ManifestReferenceRepaired,
            result.EffectiveOnNextTurn,
        };
    }

    private static string RequireDocument(AgentStateArgs args)
        => string.IsNullOrWhiteSpace(args.Document)
            ? throw new ArgumentException(
                $"document is required. Valid documents: {string.Join(", ", AgentSelfStateDocuments.All)}.",
                nameof(args))
            : args.Document.Trim();

    private static string NormalizeAction(string? action)
        => string.IsNullOrWhiteSpace(action)
            ? "inspect"
            : action.Trim().ToLowerInvariant();

    private static string ToJson(object value)
        => JsonSerializer.Serialize(value, JsonOptions);
}

public sealed record AgentStateArgs
{
    [ToolParam("Action: inspect, diagnose, read, or update. Defaults to inspect.")]
    public string? Action { get; init; }

    [ToolParam("Document: soul, agents, tools, bootstrap, memory, or heartbeat.")]
    public string? Document { get; init; }

    [ToolParam("Full replacement Markdown content. Required for update.")]
    public string? Content { get; init; }

    [ToolParam("Optional SHA-256 returned by read. Update fails if the document changed.")]
    public string? ExpectedSha256 { get; init; }

    [ToolParam("Maximum characters returned by read. Range: 1-200000; default 100000.")]
    public int? MaxChars { get; init; }
}
