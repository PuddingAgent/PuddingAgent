using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PuddingCode.Abstractions;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services.Diagnostics;

namespace PuddingPlatform.Services;

public sealed class ChatSystemCommandService
{
    private readonly ChatTranscriptWriter _transcriptWriter;
    private readonly ISessionStateManager _ssm;
    private readonly IRuntimeControlService _runtimeControl;
    private readonly IContextCompactionService _contextCompactionService;
    private readonly IToolAuthorizationService _toolAuthorizationService;
    private readonly IPuddingToolCatalogService _toolCatalog;
    private readonly IToolPermissionPolicyService _toolPermissionPolicy;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ChatTelemetryRecorder _telemetry;
    private readonly ILogger<ChatSystemCommandService> _logger;

    public ChatSystemCommandService(
        ChatTranscriptWriter transcriptWriter,
        ISessionStateManager ssm,
        IRuntimeControlService runtimeControl,
        IContextCompactionService contextCompactionService,
        IToolAuthorizationService toolAuthorizationService,
        IPuddingToolCatalogService toolCatalog,
        IToolPermissionPolicyService toolPermissionPolicy,
        IHostApplicationLifetime appLifetime,
        ChatTelemetryRecorder telemetry,
        ILogger<ChatSystemCommandService> logger)
    {
        _transcriptWriter = transcriptWriter;
        _ssm = ssm;
        _runtimeControl = runtimeControl;
        _contextCompactionService = contextCompactionService;
        _toolAuthorizationService = toolAuthorizationService;
        _toolCatalog = toolCatalog;
        _toolPermissionPolicy = toolPermissionPolicy;
        _appLifetime = appLifetime;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<IActionResult> HandleSystemCommandAsync(
        string workspaceId,
        AdminChatRequest req,
        RuntimeTraceContext trace,
        string userExternalId,
        CancellationToken ct)
    {
        var chatSessionId = req.SessionId ?? Guid.NewGuid().ToString("N");
        var messageId = Guid.NewGuid().ToString("N");
        var userCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var responseCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var trimmedCommand = req.MessageText.Trim();
        var frameTrace = trace.WithSession(chatSessionId, workspaceId).WithAgent(req.AgentId, req.AgentId);

        var responseText = await BuildSystemCommandResponseAsync(
            trimmedCommand,
            workspaceId,
            chatSessionId,
            req.AgentId ?? string.Empty,
            userExternalId,
            ct);

        return await HandleEngineResponseAsync(
            workspaceId,
            req,
            trace,
            responseText,
            sourceType: "system_command",
            sourceName: "System",
            ct,
            chatSessionId,
            messageId,
            userCreatedAt,
            responseCreatedAt,
            trimmedCommand);
    }

    public async Task<IActionResult> HandleEngineResponseAsync(
        string workspaceId,
        AdminChatRequest req,
        RuntimeTraceContext trace,
        string responseText,
        string sourceType,
        string sourceName,
        CancellationToken ct,
        string? chatSessionId = null,
        string? messageId = null,
        long? userCreatedAt = null,
        long? responseCreatedAt = null,
        string? userMessageText = null)
    {
        chatSessionId ??= req.SessionId ?? Guid.NewGuid().ToString("N");
        messageId ??= Guid.NewGuid().ToString("N");
        userCreatedAt ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        responseCreatedAt ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        userMessageText ??= req.MessageText.Trim();
        var frameTrace = trace.WithSession(chatSessionId, workspaceId).WithAgent(req.AgentId, req.AgentId);

        await _transcriptWriter.PersistMessageAsync(
            chatSessionId,
            role: "user",
            content: userMessageText,
            createdAt: userCreatedAt.Value,
            thinkingJson: null,
            usageJson: null,
            workspaceId: workspaceId,
            agentInstanceId: req.AgentId,
            agentTemplateId: req.AgentId,
            ct: ct);

        await _transcriptWriter.PersistMessageAsync(
            chatSessionId,
            role: "agent",
            content: responseText,
            createdAt: responseCreatedAt.Value,
            thinkingJson: null,
            usageJson: null,
            workspaceId: workspaceId,
            agentInstanceId: req.AgentId,
            agentTemplateId: req.AgentId,
            ct: ct);

        await _ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Metadata, new
            {
                sessionId = chatSessionId,
                messageId,
                agentId = req.AgentId,
                sourceType,
                sourceId = "system",
                sourceName,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.metadata");

        await _ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Delta, new
            {
                delta = responseText,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.delta");

        await _ssm.AppendAsync(
            chatSessionId,
            workspaceId,
            ServerSentEventFrame.Json(SseEventTypes.Done, new
            {
                messageId,
                sessionId = chatSessionId,
                reply = responseText,
            }),
            ct,
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            "chat.command.done");

        await _ssm.MarkStreamCompleteAsync(chatSessionId, ct);
        await _telemetry.RecordTimelineAsync(
            frameTrace,
            RuntimeActivityComponents.AgentExecution,
            sourceType == "system_command" ? "chat.system_command.handled" : "chat.runtime_control.handled",
            sourceType == "system_command" ? "chat.command" : "chat.runtime_control",
            RuntimeActivityStatuses.Succeeded,
            metadata: new Dictionary<string, string>
            {
                ["agentId"] = req.AgentId ?? "",
                ["messageLength"] = userMessageText.Length.ToString(),
            },
            ct: ct);
        await _telemetry.RecordTelemetryMetricAsync(
            frameTrace,
            TelemetryMetricCategories.Session,
            "session.system_command.handled",
            TelemetryMetricStatuses.Succeeded,
            durationMs: null,
            countValue: 1,
            dimensions: new Dictionary<string, string>
            {
                ["agent_id"] = req.AgentId ?? "",
                ["message_id"] = messageId,
            },
            ct: ct);

        _logger.LogInformation(
            "[Chat:EngineResponse] Handled ws={Workspace} session={Session} agent={AgentId} source={SourceType} message={Message}",
            workspaceId,
            chatSessionId,
            req.AgentId,
            sourceType,
            userMessageText);

        return new OkObjectResult(new { messageId, sessionId = chatSessionId });
    }

    public async Task<string> BuildSystemCommandResponseAsync(
        string commandText,
        string workspaceId,
        string sessionId,
        string agentId,
        string userExternalId,
        CancellationToken ct)
    {
        if (!SystemCommandParser.TryParse(commandText, out var command))
            return "Unknown system command. Send /help for available commands.";

        var result = await ProcessSystemCommandAsync(
            command,
            workspaceId,
            sessionId,
            agentId,
            userExternalId,
            ct);

        return result.Message;
    }

    public async Task<SystemCommandProcessingResult> ProcessSystemCommandAsync(
        SystemCommand command,
        string workspaceId,
        string sessionId,
        string agentId,
        string userExternalId,
        CancellationToken ct)
    {
        if (command.Action == SystemCommandAction.Help)
            return SystemCommandProcessingResult.Stop(
                ToolAuthorizationDefaults.BuildHelpMessage(command.TargetId));

        switch (command.CommandKind)
        {
            case SystemCommandKind.Compact:
                return await HandleCompactCommand(workspaceId, sessionId, agentId, ct);
            case SystemCommandKind.Memory:
                return SystemCommandProcessingResult.Stop(
                    "Command '/memory' is recognized, but this feature is not implemented yet.");
            case SystemCommandKind.Status:
                return SystemCommandProcessingResult.Stop(
                    BuildStatusMessage(_runtimeControl.GetStatus(sessionId), sessionId, agentId));
            case SystemCommandKind.Stop:
                return HandleStopCommand(command, sessionId);
            case SystemCommandKind.Mode:
                return HandleModeCommand(command);
            case SystemCommandKind.Yolo:
                return HandleYoloCommand(sessionId, agentId);
            case SystemCommandKind.EmergencyStop:
                return HandleEmergencyStop();
            default:
                if (command.RawText?.StartsWith("/resume", StringComparison.OrdinalIgnoreCase) == true)
                    return HandleResumeCommand(command, sessionId);
                return await HandleToolAuthorizationFallback(
                    command, workspaceId, sessionId, agentId, userExternalId, ct);
        }
    }

    public async Task<SystemCommandProcessingResult> HandleCompactCommand(
        string workspaceId, string sessionId, string agentId, CancellationToken ct)
    {
        var compactResult = await _contextCompactionService.CompactAsync(
            new ContextCompactionRequest(
                workspaceId,
                sessionId,
                string.IsNullOrWhiteSpace(agentId) ? null : agentId,
                ContextCompactionMode.Manual,
                ContextCompactionLevel.Full,
                "user command /compact"),
            ct);
        return SystemCommandProcessingResult.Stop(BuildCompactResultMessage(compactResult));
    }

    public SystemCommandProcessingResult HandleStopCommand(SystemCommand command, string sessionId)
    {
        var stopResult = string.Equals(command.TargetId, "all", StringComparison.Ordinal)
            ? _runtimeControl.StopAll("user command /stop all")
            : _runtimeControl.StopSession(sessionId, "user command /stop");
        return SystemCommandProcessingResult.Stop(stopResult.Message);
    }

    public SystemCommandProcessingResult HandleModeCommand(SystemCommand command)
    {
        if (string.Equals(command.TargetId, "list", StringComparison.Ordinal))
            return SystemCommandProcessingResult.Stop(BuildModeListMessage(_runtimeControl.Mode));

        if (string.Equals(command.TargetId, "safe", StringComparison.Ordinal))
            return SystemCommandProcessingResult.Stop(
                _runtimeControl.SetMode(RuntimeExecutionMode.Safe, "user command /mode safe").Message);

        if (string.Equals(command.TargetId, "normal", StringComparison.Ordinal))
            return SystemCommandProcessingResult.Stop(
                _runtimeControl.SetMode(RuntimeExecutionMode.Normal, "user command /mode normal").Message);

        return SystemCommandProcessingResult.Stop(
            $"Current runtime mode: {_runtimeControl.Mode}. Send /mode list for examples.");
    }

    public SystemCommandProcessingResult HandleYoloCommand(string sessionId, string agentId)
    {
        var yoloResult = _runtimeControl.SetMode(RuntimeExecutionMode.Yolo, "user command /yolo");
        _logger.LogWarning(
            "[Chat:Yolo] YOLO mode activated — all tool permission checks bypassed. Session={SessionId} Agent={AgentId}",
            sessionId, agentId);
        return SystemCommandProcessingResult.Stop(yoloResult.Message);
    }

    public SystemCommandProcessingResult HandleEmergencyStop()
    {
        _runtimeControl.SetMode(RuntimeExecutionMode.EmergencyStopping, "user command /estop");
        _runtimeControl.StopAll("emergency stop");
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            _appLifetime.StopApplication();
        });
        return SystemCommandProcessingResult.Stop(
            "Emergency stop accepted. Runtime is rejecting new messages, cancelling active sessions and stopping the backend.");
    }

    public SystemCommandProcessingResult HandleResumeCommand(SystemCommand command, string sessionId)
    {
        var sessionToResume = string.IsNullOrWhiteSpace(command.TargetId) || command.TargetId == "resume"
            ? sessionId
            : command.TargetId;
        var resumeResult = _runtimeControl.ResetSessionFault(sessionToResume);
        return SystemCommandProcessingResult.Stop(resumeResult.Message);
    }

    public async Task<SystemCommandProcessingResult> HandleToolAuthorizationFallback(
        SystemCommand command,
        string workspaceId,
        string sessionId,
        string agentId,
        string userExternalId,
        CancellationToken ct)
    {
        var descriptor = _toolCatalog.ListTools()
            .FirstOrDefault(t => t.ToolId.Equals(command.TargetId, StringComparison.OrdinalIgnoreCase));

        if (descriptor is null)
            return SystemCommandProcessingResult.Stop(
                $"Unknown tool '{command.TargetId}'. Send /help for available commands.");

        if (!_toolPermissionPolicy.RequiresRuntimeAuthorization(descriptor))
            return SystemCommandProcessingResult.Stop(
                $"Tool '{command.TargetId}' does not require runtime authorization.");

        if (string.IsNullOrWhiteSpace(agentId))
            return SystemCommandProcessingResult.Stop(
                $"Select an agent before authorizing tool '{command.TargetId}'.");

        var result = await _toolAuthorizationService.ApplyCommandAsync(
            command.ToToolAuthorizationCommand(),
            new ToolAuthorizationContext
            {
                WorkspaceId = workspaceId,
                SessionId = sessionId,
                AgentInstanceId = agentId,
                UserId = userExternalId,
                ToolId = command.TargetId,
            },
            ct);

        _logger.LogInformation(
            "[Chat:SystemCommandAuth] action={Action} tool={ToolId} scope={Scope} workspace={WorkspaceId} session={SessionId} agent={AgentId} user={UserId} message={Message}",
            command.Action,
            command.TargetId,
            command.Scope,
            workspaceId,
            sessionId,
            agentId,
            userExternalId,
            result.Message);

        return SystemCommandProcessingResult.Continue(
            result.Message,
            BuildAgentSystemCommandMessage(command, result.Message));
    }

    public static string BuildAgentSystemCommandMessage(
        SystemCommand command,
        string engineMessage)
    {
        var scopeText = command.Action == SystemCommandAction.Authorize
            ? command.Scope switch
            {
                ToolAuthorizationScope.Once => "once",
                ToolAuthorizationScope.Session => "session",
                ToolAuthorizationScope.Permanent => "permanent",
                _ => $"{Math.Ceiling(command.Duration.TotalMinutes):0} minutes",
            }
            : "not applicable";

        return
            "The user sent a system command. The execution engine intercepted and processed it before routing this message to you." +
            Environment.NewLine + Environment.NewLine +
            $"User command: `{command.RawText}`" + Environment.NewLine +
            $"Execution engine result: {engineMessage}" + Environment.NewLine +
            $"Command target: `{command.TargetId}`" + Environment.NewLine +
            $"Command action: `{command.Action.ToString().ToLowerInvariant()}`" + Environment.NewLine +
            $"Authorization scope: `{scopeText}`" + Environment.NewLine + Environment.NewLine +
            "Continue the conversation naturally. If this command authorized a tool you need, you may use that tool now.";
    }

    public static string BuildModeListMessage(RuntimeExecutionMode currentMode)
        => "Runtime modes:" + Environment.NewLine + Environment.NewLine +
           $"- Current: `{currentMode}`" + Environment.NewLine +
           "- `normal` - Allow normal Agent and Tool scheduling. Example: `/mode normal`" + Environment.NewLine +
           "- `safe` - Block new Agent messages, Agent starts, and Tool calls. Example: `/mode safe`" + Environment.NewLine +
           "- `yolo` - Bypass all tool permission checks (memory-only, lost on restart). Example: `/yolo`" + Environment.NewLine +
           "- `emergency_stopping` - Backend is shutting down. Trigger: `/estop`";

    public static string BuildCompactResultMessage(ContextCompactionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Context compacted.");
        sb.AppendLine();
        sb.AppendLine($"- Session: `{result.SessionId}`");
        sb.AppendLine($"- Mode: `{result.Mode}`");
        sb.AppendLine($"- Level: `{result.Level}`");
        sb.AppendLine($"- Compacted messages: {result.CompactedMessageCount}");
        sb.AppendLine($"- Tokens: {result.BeforeTokens} -> {result.AfterTokens}");
        if (result.Diagnostics is { } diag)
        {
            sb.AppendLine();
            sb.AppendLine("Diagnostics:");
            sb.AppendLine($"- Compaction ID: `{diag.CompactionId}`");
            sb.AppendLine($"- Previous session: `{diag.PreviousSessionId}`");
            if (!string.IsNullOrWhiteSpace(diag.PreviousLastMessageId))
                sb.AppendLine($"- Previous last message: `{diag.PreviousLastMessageId}`");
            sb.AppendLine($"- Previous session size: {diag.BeforeTokens} tokens / {diag.ActiveMessageCountBefore} messages");
            sb.AppendLine($"- Summary size: {diag.SummaryCharacterCount} chars / {diag.SummaryEstimatedTokens} tokens");
            sb.AppendLine($"- Summary generator: `{diag.SummaryGenerator}`");
            sb.AppendLine($"- Completed at: `{diag.CompletedAtUtc}`");
            sb.AppendLine($"- Duration: {diag.DurationMs} ms");
        }

        if (string.IsNullOrWhiteSpace(result.SummaryPreview))
        {
            sb.AppendLine("- Summary: no eligible messages required compaction.");
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(result.SummaryPreview);
        }

        return sb.ToString();
    }

    public static string BuildStatusMessage(RuntimeStatusSnapshot snapshot, string sessionId, string agentId)
    {
        var session = snapshot.Session;
        var sb = new StringBuilder();
        sb.AppendLine("Runtime status snapshot:");
        sb.AppendLine();
        sb.AppendLine($"- Runtime: mode={snapshot.Mode}, capturedAt={snapshot.CapturedAtUtc:O}, activeSessions={snapshot.ActiveSessions}");
        sb.AppendLine($"- Session: id={sessionId}, state={session?.State.ToString() ?? "Unknown"}, recentErrors={session?.RecentErrorCount ?? 0}, windowErrors={session?.WindowErrorCount ?? 0}, sameFingerprint={session?.SameFingerprintCount ?? 0}");
        sb.AppendLine($"- Agent: id={(string.IsNullOrWhiteSpace(agentId) ? "(none)" : agentId)}");
        sb.AppendLine("- Model: configured by current Agent template/provider dispatch.");
        sb.AppendLine("- Skill: available through current Agent template and registered tool catalog.");
        sb.AppendLine("- Tool: calls are allowed only when runtime mode is normal and the session is not faulted.");
        sb.AppendLine("- Safety: high-risk tools still require explicit authorization; safe mode blocks all tools.");
        sb.AppendLine("- Resource: token/tool counters are recorded in runtime telemetry and session trace logs.");
        sb.AppendLine("- Recovery: use `/stop`, start a new session, or `/mode normal` after safe mode. Faulted sessions remain blocked.");
        if (!string.IsNullOrWhiteSpace(session?.FaultSummary))
        {
            sb.AppendLine();
            sb.AppendLine(session.FaultSummary);
        }
        if (session?.RecentErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Recent errors:");
            foreach (var error in session.RecentErrors.TakeLast(5))
                sb.AppendLine($"- {error.TimestampUtc:O} {error.Kind}/{error.Component}: {error.Message}");
        }
        return sb.ToString();
    }
}

public sealed record SystemCommandProcessingResult(
    string Message,
    bool ContinueToAgent,
    string AgentMessageText)
{
    public static SystemCommandProcessingResult Stop(string message)
        => new(message, ContinueToAgent: false, AgentMessageText: message);

    public static SystemCommandProcessingResult Continue(string message, string agentMessageText)
        => new(message, ContinueToAgent: true, AgentMessageText: agentMessageText);
}
