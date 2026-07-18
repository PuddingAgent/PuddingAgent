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
    IChatCommandStore commandStore,
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

        try
        {
            logger.LogInformation(
                "[Coordinator] Start run={RunId} cmd={CmdId} turn={TurnId}",
                lease.RunId, lease.CommandId, lease.TurnId);

            var command = await commandStore.GetAsync(lease.CommandId, ctsRun.Token)
                ?? throw new InvalidOperationException($"Command {lease.CommandId} not found.");

            var profile = await profileResolver.ResolveAsync(
                lease.WorkspaceId, command.AgentInstanceId, ctsRun.Token);

            var snapshot = await snapshotFactory.CreateAsync(
                lease.WorkspaceId, command.AgentInstanceId, null, ctsRun.Token);

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
                    MessageId: null,
                    CorrelationId: lease.ConversationId,
                    CausationId: lease.TurnId,
                    ProducerEventId: null,
                    Payload: startedDoc.RootElement),
                ctsRun.Token);

            // Start monitor (lease renewal + cancel detection)
            var monitorTask = MonitorAsync(lease, ctsRun, ctsMonitor.Token);

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
                LlmConfig: profile.LlmConfig,
                ChannelId: command.ChannelId,
                UserExternalId: command.UserId,
                RunCancellation: new RunCancellation(ctsRun.Token));

            // Execute — terminal pending goes directly to CommitTerminalAsync
            var (terminalPending, terminalInfo) = await ExecuteLoopAsync(
                lease, context, ctsRun.Token);

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
                var term = TurnTerminal.Cancelled;
                var result = await journal.CommitTerminalAsync(lease, term, [], CancellationToken.None);
                return Outcome(lease, term, result.LastSequence);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Coordinator] Cancel terminal write failed");
                return Outcome(lease, TurnTerminal.Cancelled, 0);
            }
        }
        catch (Exception ex)
        {
            await SafeCancelAsync(ctsMonitor);
            logger.LogError(ex, "[Coordinator] Failed run={RunId}", lease.RunId);
            try
            {
                var term = TurnTerminal.ProtocolError(ex.Message);
                var result = await journal.CommitTerminalAsync(lease, term, [], CancellationToken.None);
                return Outcome(lease, term, result.LastSequence);
            }
            catch (Exception storeEx)
            {
                logger.LogError(storeEx, "[Coordinator] Error terminal write failed");
                return Outcome(lease, TurnTerminal.ProtocolError("terminal write failed"), 0);
            }
        }
    }

    private async Task MonitorAsync(
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
                        await controlInbox.AcknowledgeAsync(lease, msg.ControlId, CancellationToken.None);
                        ctsRun.Cancel();
                        return;
                    }
                    // Steering is acked but not consumed here — Runtime must poll Inbox itself
                    await controlInbox.AcknowledgeAsync(lease, msg.ControlId, CancellationToken.None);
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
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task<(IReadOnlyList<NewConversationEvent> Pending, TurnTerminalInfo Terminal)> ExecuteLoopAsync(
        ExecutionLease lease, TurnExecutionContext context, CancellationToken runToken)
    {
        var chunker = new TurnOutputChunker();
        TurnTerminalInfo? terminal = null;
        IReadOnlyList<NewConversationEvent> terminalPending = Array.Empty<NewConversationEvent>();

        await foreach (var evt in turnExecutor.ExecuteAsync(context, runToken))
        {
            var batch = chunker.Feed(evt, lease.ConversationId, lease.WorkspaceId,
                lease.TurnId, lease.CommandId, lease.RunId, null);

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
                await journal.AppendOutputAsync(lease, batch, runToken);
        }

        // Flush any remaining buffer and combine with terminal batch
        var flush = chunker.Flush(lease.ConversationId, lease.WorkspaceId,
            lease.TurnId, lease.CommandId, lease.RunId, null);

        var allPending = terminalPending.Concat(flush).ToList();

        if (terminal is null)
        {
            terminal = TurnTerminalInfo.Failure(
                TerminalErrorCodes.ExecutionProtocolError,
                "Runtime produced no terminal event.");
        }

        return (allPending, terminal);
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
}
