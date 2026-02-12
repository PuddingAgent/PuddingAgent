using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Agent-facing roster reader for discovering messageable agents.
/// </summary>
[Tool(
    id: "list_agents",
    name: "列出 Agent",
    description: "列出当前工作空间或房间中可发送消息的 Agent。",
    category: ToolCategory.Messaging,
    permission: ToolPermissionLevel.Low)]
public sealed class ListAgentsTool : PuddingToolBase<ListAgentsArgs>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IAgentRosterProvider? _rosterProvider;
    private readonly IServiceScopeFactory? _scopeFactory;

    public ListAgentsTool(IAgentRosterProvider rosterProvider) => _rosterProvider = rosterProvider;

    [ActivatorUtilitiesConstructor]
    public ListAgentsTool(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        ListAgentsArgs args, ToolExecutionContext context, CancellationToken ct)
    {
        try
        {
            var roomId = args.RoomId ?? "default";
            var includeBusy = args.IncludeBusy ?? true;
            var includeFrozen = args.IncludeFrozen ?? false;
            var agents = await ListAsync(context.WorkspaceId, roomId, includeBusy, includeFrozen, ct);
            return ToolExecutionResult.Ok(JsonSerializer.Serialize(new
            {
                status = "ok",
                count = agents.Count,
                workspaceId = context.WorkspaceId,
                roomId,
                agents,
            }, JsonOptions));
        }
        catch (Exception ex)
        {
            return ToolExecutionResult.Fail(ex.Message);
        }
    }

    private async Task<IReadOnlyList<AgentRosterItem>> ListAsync(string wid, string rid, bool includeBusy, bool includeFrozen, CancellationToken ct)
    {
        if (_rosterProvider is not null)
            return await _rosterProvider.ListAgentsAsync(wid, rid, includeBusy, includeFrozen, ct);

        if (_scopeFactory is null)
            throw new InvalidOperationException("Agent roster provider is not configured.");

        using var scope = _scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAgentRosterProvider>()
            .ListAgentsAsync(wid, rid, includeBusy, includeFrozen, ct);
    }
}

public sealed record ListAgentsArgs
{
    [ToolParam("Room id filter. Defaults to 'default'.")]
    public string? RoomId { get; init; }
    [ToolParam("Include busy agents. Defaults to true.")]
    public bool? IncludeBusy { get; init; }
    [ToolParam("Include frozen agents. Defaults to false.")]
    public bool? IncludeFrozen { get; init; }
}
