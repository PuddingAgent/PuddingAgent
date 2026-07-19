using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingPlatform.Services.AgentChat;

/// <summary>
/// ADR-059: Execution Run Coordinator — correct lifecycle.
/// Terminal pending output never goes through AppendOutputAsync.
/// Cancel read from Inbox, Steering forwarded to Runtime context.
/// </summary>
public sealed class ExecutionRunCoordinator(
    IExecutionLeaseStore leaseStore,
    IExecutionJournal journal,
    ITurnExecutor turnExecutor,
    IAgentRuntimeProfileResolver profileResolver,
    IAgentExecutionSnapshotFactory snapshotFactory,
    IChatMessageRepository messageRepository,
    IExecutionCommandReader commandReader,
    IControlInbox controlInbox,
    ILogger<ExecutionRunCoordinator> logger) : IExecutionRunCoordinator
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CancelPollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<ExecutionRunOutcome> ExecuteAsync(
        ExecutionLease lease, CancellationToken hostStoppingToken)
    {
        using var ctsRun = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
        using var ctsMonitor = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken);
        var chunker = new TurnOutputChunker();
        var uncommittedOutput = new List<NewConversationEvent>();
        IReadOnlyList<NewConversationEvent> terminalPending = [];
        Task<ControlMonitorOutcome>? monitorTask = null;
        var runStarted = false;
        ExecutionCommandRecord? command = null;

        try
        {
            logger.LogInformation(
                "[Coordinator] Start run={RunId} cmd={CmdId} turn={TurnId}",
                lease.RunId, lease.CommandId, lease.TurnId);

            command = await commandReader.GetAsync(lease.CommandId, ctsRun.Token)
                ?? throw new InvalidOperationException($"Command {lease.CommandId} not found.");

            var profile = await profileResolver.ResolveAsync(
                lease.WorkspaceId, command.AgentInstanceId, ctsRun.Token);

            var snapshot = await snapshotFactory.CreateAsync(
                profile, null, ctsRun.Token);
            var providerId = RequireRoutingValue(snapshot.ProviderId, "provider", command.AgentInstanceId);
            var modelId = RequireRoutingValue(snapshot.ModelId, "model", command.AgentInstanceId);
            var llmProfile = new LlmInvocationProfile
            {
                ProviderId = providerId,
                ProfileId = string.IsNullOrWhiteSpace(snapshot.ProfileId)
                    ? $"agent:{command.AgentInstanceId}:conscious"
                    : snapshot.ProfileId!,
                ModelId = modelId,
                Role = "conscious",
            };

            // StartRun: Run/Command/Turn → running + turn.started
            using var startedDoc = JsonDocument.Parse(
                $"{{\"commandId\":\"{lease.CommandId}\",\"turnId\":\"{lease.TurnId}\",\"runId\":\"{lease.RunId}\"}}");
            await journal.StartRunAsync(lease, snapshot.SnapshotId,
                new NewConversationEvent(
                    EventId: Guid.NewGuid().ToString("N"),
                    Type: ConversationEventTypes.TurnStarted,
                    SchemaVersion: 1,
                    WorkspaceId: lease.WorkspaceId,
                    TurnId: lease.TurnId,
                    CommandId: lease.CommandId,
                    RunId: lease.RunId,
                    MessageId: command.AssistantMessageId,
                    CorrelationId: lease.ConversationId,
                    CausationId: lease.TurnId,
                    ProducerEventId: null,
                    Payload: startedDoc.RootElement.Clone()),
                ctsRun.Token);
            runStarted = true;

            // Start monitor (lease renewal + cancel detection)
            monitorTask = MonitorAsync(lease, ctsRun, ctsMonitor.Token);

            // Build execution context
            var userMessage = await messageRepository.GetByMessageIdAsync(
                command.UserMessageId, ctsRun.Token);

            var context = new TurnExecutionContext(
                ConversationId: lease.ConversationId,
                WorkspaceId: lease.WorkspaceId,
                TurnId: lease.TurnId,
                CommandId: lease.CommandId,
                RunId: lease.RunId,
                AgentInstanceId: command.AgentInstanceId,
                AgentTemplateId: profile.SourceTemplateId,
                MessageText: userMessage?.Content ?? "",
                UserId: command.UserId,
                CapabilityPolicy: snapshot.CapabilityPolicy,
                ToolDefinitions: profile.ToolDefinitions,
                SkillPackages: profile.SkillPackages,
                LlmProfile: llmProfile,
                LlmConfig: profile.LlmConfig,
                MaxRounds: snapshot.BudgetMaxRounds,
                MaxElapsedSeconds: snapshot.Timeout is { } timeout
                    ? (int)Math.Ceiling(timeout.TotalSeconds)
                    : null,
                MaxToolCallsTotal: snapshot.BudgetMaxToolCalls,
                ChannelId: command.ChannelId,
                UserExternalId: command.UserId,
                RunCancellation: new RunCancellation(ctsRun.Token))
            {
                ExecutionIdentity = new RuntimeExecutionIdentity
                {
                    Kind = RuntimeExecutionKind.ConversationTurn,
                    ConversationId = lease.ConversationId,
                    TurnId = lease.TurnId,
                    CommandId = lease.CommandId,
                    RunId = lease.RunId,
                    MessageId = command.AssistantMessageId,
                },
            };

            // Execute — terminal pending goes directly to CommitTerminalAsync
            var loopResult = await ExecuteLoopAsync(
                lease,
                context,
                command.AssistantMessageId,
                chunker,
                uncommittedOutput,
                ctsRun.Token);
            terminalPending = loopResult.Pending;
            var terminalInfo = loopResult.Terminal;

            await SafeCancelAsync(ctsMonitor);
            try { await monitorTask; } catch { }

            var terminal = ConvertTerminalInfo(terminalInfo);
            var result = await journal.CommitTerminalAsync(
                lease, terminal, terminalPending, CancellationToken.None);

            logger.LogInformation("[Coordinator] Completed run={RunId} kind={Kind} seq={Seq}",
                lease.RunId, terminal.Kind, result.LastSequence);

            return new ExecutionRunOutcome(
                lease.CommandId, lease.TurnId, lease.RunId,
                terminal, result.LastSequence,
                result.FirstSequence, result.LastSequence, result.Count);
        }
        catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
        {
            await SafeCancelAsync(ctsMonitor);
            await leaseStore.ReleaseAsync(lease, CancellationToken.None);
            return Outcome(lease, TurnTerminal.LeaseLost, 0);
        }
        catch (OperationCanceledException) when (ctsRun.IsCancellationRequested)
        {
            await SafeCancelAsync(ctsMonitor);
            try
            {
                var monitorOutcome = await GetMonitorOutcomeAsync(monitorTask);
                var term = monitorOutcome.LeaseLost
                    ? TurnTerminal.LeaseLost
                    : TurnTerminal.Cancelled;
                var pending = CollectPendingOutput(
                    lease,
                    command?.AssistantMessageId,
                    chunker,
                    uncommittedOutput,
                    terminalPending);
                var result = await journal.CommitTerminalAsync(
                    lease, term, pending, CancellationToken.None);
                if (monitorOutcome.CancelControlId is not null)
                {
                    await controlInbox.AcknowledgeAsync(
                        lease, monitorOutcome.CancelControlId, CancellationToken.None);
                }
                return Outcome(lease, term, result.LastSequence);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Coordinator] Cancel terminal write failed");
                var fallback = await TryCloseAfterTerminalWriteFailureAsync(
                    lease, "cancel_terminal_commit_failed", CancellationToken.None);
                return Outcome(
                    lease,
                    fallback?.Terminal ?? TurnTerminal.Cancelled,
                    fallback?.Sequence ?? 0);
            }
        }
        catch (Exception ex)
        {
            await SafeCancelAsync(ctsMonitor);
            logger.LogError(ex, "[Coordinator] Failed run={RunId}", lease.RunId);
            try
            {
                var term = ex is AgentConfigurationException configurationError
                    ? TurnTerminal.Failure(
                        configurationError.ErrorCode,
                        configurationError.Message)
                    : TurnTerminal.ProtocolError(ex.Message);
                var pending = CollectPendingOutput(
                    lease,
                    command?.AssistantMessageId,
                    chunker,
                    uncommittedOutput,
                    terminalPending);
                var result = runStarted
                    ? await journal.CommitTerminalAsync(
                        lease, term, pending, CancellationToken.None)
                    : await journal.TryCommitInfrastructureFailureAsync(
                        lease, term, pending, CancellationToken.None)
                        ?? throw new InvalidOperationException(
                            $"Infrastructure terminal fence rejected run={lease.RunId}.");
                return Outcome(lease, term, result.LastSequence);
            }
            catch (Exception storeEx)
            {
                logger.LogError(storeEx, "[Coordinator] Error terminal write failed");
                var fallback = await TryCloseAfterTerminalWriteFailureAsync(
                    lease, "terminal_commit_failed", CancellationToken.None);
                return Outcome(
                    lease,
                    fallback?.Terminal ?? TurnTerminal.ProtocolError("terminal write failed"),
                    fallback?.Sequence ?? 0);
            }
        }
    }

    private async Task<TerminalFallbackResult?> TryCloseAfterTerminalWriteFailureAsync(
        ExecutionLease lease,
        string errorCode,
        CancellationToken ct)
    {
        var terminal = TurnTerminal.Failure(
            errorCode,
            "Execution output could not be committed; the run was closed to preserve lifecycle consistency.");
        try
        {
            var result = await journal.TryCommitInfrastructureFailureAsync(
                lease, terminal, [], ct);
            return result is null
                ? null
                : new TerminalFallbackResult(terminal, result.LastSequence);
        }
        catch (Exception fallbackEx)
        {
            logger.LogCritical(
                fallbackEx,
                "[Coordinator] Infrastructure terminal fallback failed run={RunId} fence={Fence}",
                lease.RunId,
                lease.FencingToken);
            return null;
        }
    }

    private static string RequireRoutingValue(string? value, string field, string agentId)
        => !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new AgentConfigurationException(
                agentId,
                $"Agent '{agentId}' does not have a resolved LLM {field}.");

    private async Task<ControlMonitorOutcome> MonitorAsync(
        ExecutionLease lease, CancellationTokenSource ctsRun, CancellationToken ct)
    {
        var lastLeaseRenew = Environment.TickCount64;
        var leaseIntervalMs = (long)LeaseDuration.TotalMilliseconds / 2;
        long controlCursor = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(CancelPollInterval, ct);

                // Poll control inbox for cancel
                var msgs = await controlInbox.ReadPendingAsync(lease, controlCursor, CancellationToken.None);
                foreach (var msg in msgs)
                {
                    controlCursor = Math.Max(controlCursor, msg.Sequence);
                    if (msg.Kind == ControlMessageKind.CancelRequested)
                    {
                        logger.LogWarning("[Coordinator] CancelRequested from inbox run={RunId}", lease.RunId);
                        ctsRun.Cancel();
                        return new ControlMonitorOutcome(false, msg.ControlId);
                    }
                    logger.LogWarning(
                        "[Coordinator] Control remains pending because Runtime has no consumer kind={Kind} controlId={ControlId}",
                        msg.Kind, msg.ControlId);
                }

                // Renew lease based on timer, not control sequence
                var nowTicks = Environment.TickCount64;
                if (nowTicks - lastLeaseRenew >= leaseIntervalMs)
                {
                    lastLeaseRenew = nowTicks;
                    var renewed = await leaseStore.RenewAsync(lease, LeaseDuration, CancellationToken.None);
                    if (!renewed)
                    {
                        logger.LogWarning("[Coordinator] Lease lost run={RunId}", lease.RunId);
                        ctsRun.Cancel();
                        return new ControlMonitorOutcome(true, null);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return new ControlMonitorOutcome(false, null);
    }

    private async Task<(IReadOnlyList<NewConversationEvent> Pending, TurnTerminalInfo Terminal)> ExecuteLoopAsync(
        ExecutionLease lease,
        TurnExecutionContext context,
        string assistantMessageId,
        TurnOutputChunker chunker,
        List<NewConversationEvent> uncommittedOutput,
        CancellationToken runToken)
    {
        TurnTerminalInfo? terminal = null;
        IReadOnlyList<NewConversationEvent> terminalPending = Array.Empty<NewConversationEvent>();

        await foreach (var evt in turnExecutor.ExecuteAsync(context, runToken))
        {
            var batch = chunker.Feed(evt, lease.ConversationId, lease.WorkspaceId,
                lease.TurnId, lease.CommandId, lease.RunId, assistantMessageId);

            if (evt.IsTerminal)
            {
                terminal = evt.TerminalInfo;
                // batch contains pending output flushed by Chunker, NOT the terminal event
                // DO NOT write via AppendOutputAsync — pass to CommitTerminalAsync
                terminalPending = batch;
                break;
            }

            // Non-terminal batch → normal output
            if (batch.Count > 0)
            {
                uncommittedOutput.Clear();
                uncommittedOutput.AddRange(batch);
                await journal.AppendOutputAsync(lease, batch, runToken);
                uncommittedOutput.Clear();
            }
        }

        // Flush any remaining buffer and combine with terminal batch
        var flush = chunker.Flush(lease.ConversationId, lease.WorkspaceId,
            lease.TurnId, lease.CommandId, lease.RunId, assistantMessageId);

        var allPending = terminalPending.Concat(flush).ToList();

        if (terminal is null)
        {
            terminal = TurnTerminalInfo.Failure(
                TerminalErrorCodes.ExecutionProtocolError,
                "Runtime produced no terminal event.");
        }

        return (allPending, terminal);
    }

    private static IReadOnlyList<NewConversationEvent> CollectPendingOutput(
        ExecutionLease lease,
        string? assistantMessageId,
        TurnOutputChunker chunker,
        IReadOnlyList<NewConversationEvent> uncommittedOutput,
        IReadOnlyList<NewConversationEvent> terminalPending)
    {
        var buffered = chunker.Flush(
            lease.ConversationId,
            lease.WorkspaceId,
            lease.TurnId,
            lease.CommandId,
            lease.RunId,
            assistantMessageId);

        return terminalPending
            .Concat(uncommittedOutput)
            .Concat(buffered)
            .DistinctBy(e => e.EventId)
            .ToList();
    }

    private static async Task<ControlMonitorOutcome> GetMonitorOutcomeAsync(
        Task<ControlMonitorOutcome>? monitorTask)
    {
        if (monitorTask is null)
            return new ControlMonitorOutcome(false, null);

        try
        {
            return await monitorTask;
        }
        catch
        {
            return new ControlMonitorOutcome(false, null);
        }
    }

    private static TurnTerminal ConvertTerminalInfo(TurnTerminalInfo? info)
    {
        if (info is null)
            return TurnTerminal.ProtocolError("No terminal info from Runtime.");
        return info.Kind switch
        {
            TurnTerminalKind.Completed => TurnTerminal.Success(info.Reply, info.Usage),
            TurnTerminalKind.Failed => TurnTerminal.Failure(
                info.ErrorCode ?? TerminalErrorCodes.RuntimeExecutionFailed,
                info.ErrorMessage ?? "Unknown failure."),
            TurnTerminalKind.Cancelled => TurnTerminal.Cancelled,
            _ => TurnTerminal.ProtocolError("Unknown terminal kind."),
        };
    }

    private static ExecutionRunOutcome Outcome(ExecutionLease lease, TurnTerminal terminal, long seq) =>
        new(lease.CommandId, lease.TurnId, lease.RunId, terminal, seq, 0, seq, 0);

    private static async Task SafeCancelAsync(CancellationTokenSource cts)
    {
        try { await cts.CancelAsync(); } catch { }
    }

    private sealed record ControlMonitorOutcome(
        bool LeaseLost,
        string? CancelControlId);

    private sealed record TerminalFallbackResult(
        TurnTerminal Terminal,
        long Sequence);
}
