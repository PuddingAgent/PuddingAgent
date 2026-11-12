using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingDingTalk;
using PuddingPlatform.Data;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Messaging;

namespace PuddingAgent.Services;

/// <summary>Core selects the restricted execution environment for every turn in a public DingTalk room.</summary>
internal sealed class DingTalkTurnExecutor(TurnExecutorAdapter standard, IChatMessageRepository messages,
    AgentManifestCatalog agents, PuddingDataPaths paths, IRuntimeLlmClient model, ILlmConfigService configs,
    IHttpClientFactory http, ILoggerFactory logs, IAgentExecutionStateRegistry state,
    AgentExecutionAdmissionCoordinator admission, IServiceScopeFactory scopes) : ITurnExecutor
{
    public IAsyncEnumerable<TurnExecutionEvent> ExecuteAsync(TurnExecutionContext context, CancellationToken ct)
    {
        if (RequiresPublicExecution(context.Origin))
            return PublicAsync(context, ct);
        return standard.ExecuteAsync(context, ct);
    }

    internal static bool RequiresPublicExecution(MessageOrigin? origin) =>
        origin is { ChannelType: "dingtalk", IsUnboundChannelSender: true };

    private async IAsyncEnumerable<TurnExecutionEvent> PublicAsync(TurnExecutionContext context,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var row = await messages.GetByMessageIdAsync(context.InboundMessageId ?? "", ct);
        var sender = ReadSender(row);
        var settings = JsonSerializer.Deserialize<DingTalkGroupChannel.Settings>(
            await File.ReadAllTextAsync(paths.SystemConfigFile("dingtalk-group.json"), ct), JsonSerializerOptions.Web);
        if (settings is not { Enabled: true } || sender is null || sender.App != settings.ClientId ||
            sender.Agent != settings.AgentId || sender.Agent != context.AgentInstanceId || row is null ||
            row.WorkspaceId != context.WorkspaceId || row.SessionId != context.ConversationId ||
            sender.Input != row.MessageId || sender.Actor != context.UserId)
            throw new UnauthorizedAccessException("Unverified DingTalk turn.");
        var prior = await messages.GetMessagesCursorAsync(row.SessionId, row.Id, 24, ct);
        var priorIds = prior.Where(p => p.Role == "user").Select(p => p.MessageId).ToArray();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var replies = await (from command in db.ChatExecutionCommands.AsNoTracking()
            join evt in db.ConversationEvents.AsNoTracking() on command.CommandId equals evt.CommandId
            where command.SessionId == row.SessionId && command.WorkspaceId == row.WorkspaceId &&
                  command.AgentInstanceId == sender.Agent && priorIds.Contains(command.UserMessageId) &&
                  evt.ConversationId == row.SessionId && evt.Type == ConversationEventTypes.TurnCompleted
            select new { command.UserMessageId, evt.Payload }).ToDictionaryAsync(p => p.UserMessageId, p => p.Payload, ct);
        var history = PublicHistory(prior, row, replies);
        var images = ContentPartsEnvelope.Decode(row.ContentPartsJson)?.OfType<LlmImagePart>()
            .Select(p => p.ArtifactId).ToArray() ?? [];
        var runner = new DingTalkPublicAgent(agents, paths, sender.Agent, model, configs, settings.ReviewedSourceRoot,
            new GroupFeedbackInbox(Path.Combine(paths.ChannelsRoot, "dingtalk-group", "feedback")), http, logs, state,
            scope.ServiceProvider.GetRequiredService<PuddingRuntime.Services.Tools.PuddingToolSchemaService>());
        var q = new PublicQuestion(sender.Source, row.Content, sender.Message);
        using var agentLease = admission.AcquireForeground(context.WorkspaceId, sender.Agent);
        using var roomLease = admission.AcquireSessionForeground(context.WorkspaceId, row.SessionId);
        RuntimeDispatchResult result;
        while (true)
        {
            result = await runner.RunAsync(q, row.SessionId, history, images, context, ct);
            if (result.ExecutionState != AgentExecutionState.Busy) break;
            await Task.Delay(500, ct);
        }
        var toolNumber = 0;
        foreach (var step in result.TurnSteps ?? [])
        {
            if (step.ToolName is not { Length: > 0 } name) continue;
            var toolCallId = $"{context.RunId}-{++toolNumber}";
            yield return new(toolCallId + "-call", ConversationEventTypes.ToolCallRequested, 1,
                JsonSerializer.SerializeToElement(new { toolCallId, name }), false, null);
            yield return new(toolCallId + "-result", ConversationEventTypes.ToolCallCompleted, 1,
                JsonSerializer.SerializeToElement(new { toolCallId, name, success = step.ToolSuccess }), false, null);
        }
        var terminal = result.IsSuccess ? TurnTerminalInfo.Success(result.ReplyText, JsonSerializer.SerializeToElement(result.Usage))
            : TurnTerminalInfo.Failure("dingtalk.turn_failed", "群聊执行未完成。");
        yield return new(context.RunId + "-terminal", result.IsSuccess ? ConversationEventTypes.TurnCompleted : ConversationEventTypes.TurnFailed,
            1, JsonSerializer.SerializeToElement(new { reply = result.ReplyText }), true, terminal);
    }

    private static DingTalkConversationIngress.Sender? ReadSender(ChatMessageRow? row)
    {
        if (row is not { Role: "user", MetadataJson: { } json }) return null;
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return fields?.GetValueOrDefault(DingTalkConversationIngress.Provenance) is { } value
            ? JsonSerializer.Deserialize<DingTalkConversationIngress.Sender>(value) : null;
    }

    internal static string[] PublicHistory(IReadOnlyList<ChatMessageRow> prior, ChatMessageRow current,
        IReadOnlyDictionary<string, string> replies)
    {
        var items = new List<string>();
        foreach (var row in prior.Where(r => r.SessionId == current.SessionId && r.WorkspaceId == current.WorkspaceId
                     && r.Id < current.Id).OrderBy(r => r.Id))
        {
            if (string.IsNullOrWhiteSpace(row.Content)) continue;
            var sender = ReadSender(row);
            if (sender is null || sender.Input != row.MessageId ||
                !replies.TryGetValue(row.MessageId, out var terminal)) continue;
            using var payload = JsonDocument.Parse(terminal);
            if (!payload.RootElement.TryGetProperty("reply", out var value) || value.GetString() is not { } reply) continue;
            items.Add(JsonSerializer.Serialize(new { sender = sender.Source.UserId, sentAt = sender.Source.SentAt,
                question = row.Content[..Math.Min(650, row.Content.Length)], answer = reply[..Math.Min(650, reply.Length)] },
                HistoryJson));
        }
        return items.TakeLast(6).ToArray();
    }
    private static readonly JsonSerializerOptions HistoryJson = new()
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
