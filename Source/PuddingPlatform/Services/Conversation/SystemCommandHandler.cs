using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatform.Services.Conversation;

/// <summary>
/// System-command boundary. A handled command writes a user/system transcript pair,
/// but never creates an execution command, ConversationTurn, or Agent run.
/// </summary>
public sealed class SystemCommandHandler(
    PlatformDbContext db,
    IRuntimeControlService runtimeControl,
    IRequestCompactionHandler requestCompactionHandler,
    ISystemStatusSnapshotProvider statusSnapshotProvider,
    ILogger<SystemCommandHandler> logger) : ISystemCommandHandler
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<SystemCommandResult> HandleAsync(
        SystemCommandRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        var commandText = request.CommandText.Trim();
        var isParsed = SystemCommandParser.TryParse(commandText, out var command);

        var existing = await db.ChatMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(
                message =>
                    message.CommandId == request.ClientRequestId &&
                    message.MessageId == request.ResponseMessageId,
                ct);
        if (existing is not null)
        {
            // The authenticated Web endpoint historically reapplies process-local
            // YOLO state after a reset. External gateway replays must preserve the
            // originally recorded authorization result instead of gaining a new
            // privilege after whitelist changes.
            if (request.SourceChannel is null
                && request.IsPrivilegedUser
                && isParsed
                && IsYolo(command))
            {
                runtimeControl.SetMode(
                    RuntimeExecutionMode.Yolo,
                    $"idempotent replay of /yolo; user={request.UserId}; conversation={request.ConversationId}");
            }
            // A state-changing command such as /compact can move the Agent's
            // main conversation before Feishu retries the same message. Stable
            // gateway IDs remain authoritative across that conversation switch.
            return BuildResult(
                request with { ConversationId = existing.SessionId },
                existing.Content,
                runtimeControl.Mode);
        }

        string responseMessage;
        if (!isParsed)
        {
            responseMessage =
                $"Unknown Pudding command '{commandText}'. Send /help for available commands.";
        }
        else if (SystemCommandParser.RequiresPrivilege(command)
                 && !request.IsPrivilegedUser)
        {
            var channel = string.IsNullOrWhiteSpace(request.SourceChannel)
                ? "external"
                : request.SourceChannel;
            responseMessage =
                $"Permission denied: the current {channel} user is not in the configured whitelist for privileged command '{command.RawText}'.";
        }
        else if (command.Action == SystemCommandAction.Help)
        {
            responseMessage = ToolAuthorizationDefaults.BuildHelpMessage(
                command.TargetId);
        }
        else if (command.CommandKind == SystemCommandKind.WhoAmI)
        {
            responseMessage = BuildWhoAmIMessage(request);
        }
        else if (command.CommandKind == SystemCommandKind.Status)
        {
            responseMessage = await HandleStatusAsync(request, ct);
        }
        else if (command.CommandKind == SystemCommandKind.Compact
                 && command.Action == SystemCommandAction.Run)
        {
            responseMessage = await HandleCompactAsync(request, ct);
        }
        else if (IsYolo(command))
        {
            var action = runtimeControl.SetMode(
                RuntimeExecutionMode.Yolo,
                $"user command /yolo; user={request.UserId}; conversation={request.ConversationId}");
            responseMessage = action.Message;
        }
        else
        {
            responseMessage =
                $"Pudding intercepted '{command.RawText}', but this system command is not implemented yet.";
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sourceMetadata = JsonSerializer.Serialize(
            new
            {
                sourceType = "system_command",
                sourceId = "system",
                sourceName = "System",
                sourceChannel = request.SourceChannel,
                externalUserId = request.ExternalUserId,
                privilegedUser = request.IsPrivilegedUser,
            },
            JsonOptions);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            if (!await db.ChatMessages.AnyAsync(
                    message => message.MessageId == request.ClientMessageId,
                    ct))
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = request.ClientMessageId,
                    SessionId = request.ConversationId,
                    WorkspaceId = request.WorkspaceId,
                    AgentInstanceId = request.AgentId,
                    Role = "user",
                    Content = commandText,
                    TurnId = request.ClientRequestId,
                    CommandId = request.ClientRequestId,
                    UserId = request.UserId,
                    CreatedAt = now,
                });
            }

            if (!await db.ChatMessages.AnyAsync(
                    message => message.MessageId == request.ResponseMessageId,
                    ct))
            {
                db.ChatMessages.Add(new ChatMessageEntity
                {
                    MessageId = request.ResponseMessageId,
                    SessionId = request.ConversationId,
                    WorkspaceId = request.WorkspaceId,
                    AgentInstanceId = request.AgentId,
                    Role = "agent",
                    Content = responseMessage,
                    TurnId = request.ClientRequestId,
                    CommandId = request.ClientRequestId,
                    MetadataJson = sourceMetadata,
                    CreatedAt = now + 1,
                });
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        logger.LogInformation(
            "[SystemCommand] handled command={Command} parsed={Parsed} privileged={Privileged} workspace={WorkspaceId} conversation={ConversationId} user={UserId} mode={Mode}",
            commandText,
            isParsed,
            isParsed && SystemCommandParser.RequiresPrivilege(command),
            request.WorkspaceId,
            request.ConversationId,
            request.UserId,
            runtimeControl.Mode);

        return BuildResult(request, responseMessage, runtimeControl.Mode);
    }

    private static SystemCommandResult BuildResult(
        SystemCommandRequest request,
        string message,
        RuntimeExecutionMode mode) =>
        new(
            request.ConversationId,
            request.ClientMessageId,
            request.ResponseMessageId,
            request.CommandText.Trim(),
            message,
            mode.ToString());

    private static bool IsYolo(SystemCommand command)
        => command.CommandKind == SystemCommandKind.Yolo
           && command.Action == SystemCommandAction.Run;

    private async Task<string> HandleStatusAsync(
        SystemCommandRequest request,
        CancellationToken ct)
    {
        try
        {
            var snapshot = await statusSnapshotProvider.GetAsync(
                new SystemStatusSnapshotRequest(
                    request.WorkspaceId,
                    request.ConversationId,
                    request.AgentId),
                ct);
            return BuildStatusMessage(snapshot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[SystemCommand] status failed workspace={WorkspaceId} conversation={ConversationId} agent={AgentId} user={UserId}",
                request.WorkspaceId,
                request.ConversationId,
                request.AgentId,
                request.UserId);
            return "Pudding status is temporarily unavailable. Check the backend diagnostics for details.";
        }
    }

    private static string BuildStatusMessage(SystemStatusSnapshot snapshot)
    {
        var model = string.IsNullOrWhiteSpace(snapshot.ModelId)
            ? "unavailable"
            : string.IsNullOrWhiteSpace(snapshot.ProviderId)
                ? InlineCode(snapshot.ModelId)
                : InlineCode($"{snapshot.ProviderId}/{snapshot.ModelId}");
        var template = string.IsNullOrWhiteSpace(snapshot.SourceTemplateId)
            ? string.Empty
            : $" · template {InlineCode(snapshot.SourceTemplateId)}";
        var context = snapshot.ContextHealth is not { } health
            ? "unavailable"
            : $"{FormatTokens(health.RemainingTokens)} remaining / " +
              $"{FormatTokens(health.EffectiveWindowTokens)} effective " +
              $"({FormatTokens(health.UsedTokens)} used, " +
              $"{health.UsageRatio.ToString("P1", CultureInfo.InvariantCulture)}, " +
              $"{health.State}; source={health.UsageSource}, confidence={health.UsageConfidence})";
        var runtime =
            $"{InlineCode(snapshot.RuntimeMode.ToString())} · " +
            $"{snapshot.ActiveRuntimeSessions} active session(s) · " +
            $"{snapshot.SessionWindowErrorCount} error(s) in the runtime window";

        var lines = new List<string>
        {
            "**Pudding status**",
            $"- Agent: {snapshot.AgentDisplayName} ({InlineCode(snapshot.AgentId)}){template}",
            $"- Session: {InlineCode(snapshot.SessionState.ToString())} ({InlineCode(snapshot.ConversationId)}) · {snapshot.RunningSubAgents} running sub-agent(s)",
            $"- Context: {context}",
            $"- Model: {model}",
            $"- Runtime: {runtime}",
            $"- Capabilities: {snapshot.CapabilityCount}",
        };

        if (!string.IsNullOrWhiteSpace(snapshot.SessionFaultSummary))
            lines.Add("- Fault: present; inspect authenticated backend diagnostics for details");
        if (snapshot.Warnings.Count > 0)
            lines.Add($"- Warnings: {string.Join("; ", snapshot.Warnings)}");

        return string.Join('\n', lines);
    }

    private static string FormatTokens(int tokens)
        => tokens >= 1_000
            ? $"{tokens / 1_000d:0.0}k"
            : tokens.ToString(CultureInfo.InvariantCulture);

    private static string InlineCode(string value)
        => $"`{value.Replace("`", "ˋ", StringComparison.Ordinal)}`";

    private async Task<string> HandleCompactAsync(
        SystemCommandRequest request,
        CancellationToken ct)
    {
        try
        {
            var result = await requestCompactionHandler.HandleAsync(
                new RequestCompactionCommand(
                    request.ConversationId,
                    request.WorkspaceId,
                    request.AgentId,
                    ContextCompactionLevel.Full,
                    $"system command /compact from {request.SourceChannel ?? "web"}",
                    request.ClientRequestId,
                    request.UserId),
                ct);
            var title = string.IsNullOrWhiteSpace(result.NewConversationTitle)
                ? result.NewConversationId
                : result.NewConversationTitle;

            return
                $"Context compaction completed. Compacted {result.Compaction.CompactedMessageCount} messages; " +
                $"context tokens {result.Compaction.BeforeTokens} -> {result.Compaction.AfterTokens}. " +
                $"Future messages will continue in `{title}` (`{result.NewConversationId}`).";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "[SystemCommand] compact failed workspace={WorkspaceId} conversation={ConversationId} agent={AgentId} user={UserId}",
                request.WorkspaceId,
                request.ConversationId,
                request.AgentId,
                request.UserId);
            return $"Context compaction failed: {ex.Message}";
        }
    }

    private static string BuildWhoAmIMessage(SystemCommandRequest request)
    {
        if (!string.Equals(
                request.SourceChannel,
                "feishu",
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(request.ExternalUserId))
        {
            return "Feishu user ID is unavailable because this command did not originate from a verified Feishu message.";
        }

        return $"Your Feishu user ID (open_id) is `{request.ExternalUserId}`.";
    }

    private static void Validate(SystemCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkspaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientRequestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ClientMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ResponseMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CommandText);
    }
}
