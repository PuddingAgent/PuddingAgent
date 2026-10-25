using PuddingCode.Core;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Services;
using PuddingCode.SubAgents;
using PuddingCode.Tools;
using PuddingMemoryEngine.Data;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Background;
using PuddingRuntime.Services.Diagnostics;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;
using PuddingCode.Observability;
using PuddingCode.Runtime;
using Serilog.Context;

using PuddingContextPolicy;

namespace PuddingRuntime.Services;

public sealed partial class AgentExecutionService
{
    private sealed record WarmPrefixCommitBoundary(List<ChatMessage> History,
        IReadOnlyList<ChatMessage> Outbound, IReadOnlyList<LlmToolDefinition> Tools, LlmConfig? Config);

    private sealed class WarmPrefixMaintenance : IAsyncDisposable
    {
        internal BackgroundCompactionCandidate<WarmPrefixCommitBoundary, WarmPrefixCompactionOutcome>? Candidate;
        internal string? CompactionId;
        internal int LastAttemptRound = -100;
        internal int LastBoundaryRound = -1;
        internal long PreviousTokens, PreviousBytes;
        internal bool ProtectionRequired;
        internal WarmPrefixCompactionOutcome LastResult = WarmPrefixCompactionOutcome.NotNeeded;
        public async ValueTask DisposeAsync()
        {
            if (Candidate is not null) await Candidate.DisposeAsync();
        }
    }

    private async Task<WarmPrefixCompactionOutcome> TryWarmPrefixCompactionAsync(
        RuntimeDispatchRequest request, string agentInstanceId, List<ChatMessage> durableHistory,
        IReadOnlyList<ChatMessage> exactOutboundHistory, IReadOnlyList<LlmToolDefinition> llmTools,
        LlmConfig? effectiveLlmConfig, int round, CancellationToken ct, WarmPrefixMaintenance maintenance)
    {
        if (_contextUsageSnapshotStore is null) return WarmPrefixCompactionOutcome.NotNeeded;
        if (maintenance.LastBoundaryRound == round) return maintenance.LastResult;
        maintenance.LastBoundaryRound = round;
        var measured = _contextUsageSnapshotStore.MeasureLlmRequest(request.SessionId, exactOutboundHistory, llmTools, effectiveLlmConfig?.ModelId);
        var bytes = LlmRequestTextBudget.Measure(exactOutboundHistory, llmTools);
        var durableBytesBefore = LlmRequestTextBudget.Measure(durableHistory, llmTools);
        var options = _runtimeExecutionConfig?.GetOptions().ContextCompaction ?? new ContextMaintenanceOptions();
        var thresholds = new PrefixCompactionThresholds(options.PrepareRatio, options.CommitRatio,
            options.BlockingRatio, options.TargetRatio, options.MinimumEpochRounds, options.UploadBlockingRatio);
        var inputLimit = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(effectiveLlmConfig, workUnitInputCapacity: request.UsageBudget?.MaxInputTokens);
        var registeredWindow = effectiveLlmConfig?.MaxContextTokens ?? inputLimit;
        var decision = PrefixCompactionPolicy.Decide(measured.UsedTokens, registeredWindow,
            bytes, LlmRequestTextBudget.ResolveLimit(request),
            maintenance.PreviousTokens > 0 ? measured.UsedTokens - maintenance.PreviousTokens : 0,
            maintenance.PreviousBytes > 0 ? bytes - maintenance.PreviousBytes : 0,
            round - maintenance.LastAttemptRound, thresholds, inputLimit);
        maintenance.ProtectionRequired = decision.WaitForCandidate;
        maintenance.PreviousTokens = measured.UsedTokens;
        maintenance.PreviousBytes = bytes;
        // 2026-10-25：只在**真的准备压缩**时提到 Information。
        // 理由：压缩是本仓最大的缓存杀手（占缺失 token 近六成），而“为什么现在压”
        // 取决于 pressure / window / limit 三个入参；写在 Debug 上等于生产环境看不见，
        // 排查时只能反推。未触发时仍走 Debug，不污染常规日志。
        if (decision.Prepare)
            _logger.LogInformation(
                "[AgentExec:Compaction] Boundary decision session={Session} round={Round} prepare={Prepare} commit={Commit} wait={Wait} pressure={Pressure} tokens={Tokens} window={Window} bytes={Bytes} limit={Limit} growthTokens={GrowthTokens} growthBytes={GrowthBytes} reason={Reason} hasCandidate={HasCandidate} ready={Ready}",
                request.SessionId, round + 1, decision.Prepare, decision.CommitReady, decision.WaitForCandidate,
                decision.Pressure.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                measured.UsedTokens, registeredWindow, bytes, LlmRequestTextBudget.ResolveLimit(request),
                maintenance.PreviousTokens, maintenance.PreviousBytes, decision.Reason,
                maintenance.Candidate is not null, maintenance.Candidate?.IsReady);
        else
            _logger.LogDebug(
            "[AgentExec:Compaction] Boundary decision session={Session} round={Round} prepare={Prepare} commit={Commit} wait={Wait} pressure={Pressure} tokens={Tokens} window={Window} bytes={Bytes} limit={Limit} growthTokens={GrowthTokens} growthBytes={GrowthBytes} hasCandidate={HasCandidate} ready={Ready}",
            request.SessionId, round + 1, decision.Prepare, decision.CommitReady, decision.WaitForCandidate,
            decision.Pressure.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
            measured.UsedTokens, registeredWindow, bytes, LlmRequestTextBudget.ResolveLimit(request),
            maintenance.PreviousTokens, maintenance.PreviousBytes, maintenance.Candidate is not null,
            maintenance.Candidate?.IsReady);
        // At most one fresh recovery after an obsolete prepared candidate, never duplicate a summary per round.
        for (var recovery = 0; recovery < 2; recovery++)
        {
            if (maintenance.Candidate is null)
            {
                if (!decision.Prepare) return maintenance.LastResult = WarmPrefixCompactionOutcome.NotNeeded;
                var frozenHistory = durableHistory.ToList();
                var frozenOutbound = exactOutboundHistory.ToArray();
                var frozenTools = llmTools.ToArray();
                var id = $"warm-prefix:{request.ExecutionIdentity?.RunId ?? request.SessionId}:r{round + 1}:{Guid.NewGuid():N}";
                maintenance.CompactionId = id;
                maintenance.Candidate = new(async (gate, token) =>
                {
                    try
                    {
                        return await GenerateWarmPrefixCandidateAsync(request, agentInstanceId,
                            frozenHistory, frozenOutbound, frozenTools, effectiveLlmConfig, round,
                            token, !decision.WaitForCandidate, id, gate, thresholds, decision.WaitForCandidate);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        await EmitCandidateStageAsync(request, id, "cancelled", SseEventTypes.ContextCompactionSkipped,
                            "candidate_cancelled_or_expired", false);
                        return new(false, null, thresholds.Prepare, thresholds.Target, null, WarmPrefixCompactionDisposition.DeferredSoft,
                            SkipReason: "candidate_cancelled_or_expired");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[AgentExec:Compaction] Candidate preparation failed session={Session} compactionId={Id}", request.SessionId, id);
                        await EmitCandidateStageAsync(request, id, "failed", SseEventTypes.ContextCompactionFailed,
                            "candidate_preparation_failed", false);
                        return new(false, null, thresholds.Prepare, thresholds.Target, null, WarmPrefixCompactionDisposition.Failed,
                            SkipReason: "candidate_preparation_failed");
                    }
                    finally { _contextCompactionService?.EndPreparedCompaction(request.SessionId, id); }
                }, ct);
            }
            var candidate = maintenance.Candidate;
            if (decision.WaitForCandidate && !candidate.IsReady && !candidate.Completion.IsCompleted)
            {
                await EmitCandidateStageAsync(request, maintenance.CompactionId!, "waiting", SseEventTypes.ContextCompactionStarted,
                    decision.Reason, true);
                await candidate.WaitUntilReadyAsync(ct);
            }
            if (!candidate.Completion.IsCompleted && (!decision.CommitReady || !candidate.IsReady))
                return maintenance.LastResult = new(false, null, thresholds.Prepare, thresholds.Target, null,
                    WarmPrefixCompactionDisposition.DeferredSoft, SkipReason: "candidate_preparing_prefix_unchanged");

            if (candidate.IsReady && !candidate.Completion.IsCompleted)
            {
                await EmitCandidateStageAsync(request, maintenance.CompactionId!, "applying", SseEventTypes.ContextCompactionStarted,
                    decision.Reason, true);
                await candidate.CommitAtBoundaryAsync(new(durableHistory, exactOutboundHistory, llmTools, effectiveLlmConfig));
            }
            var result = await candidate.Completion;
            await candidate.DisposeAsync();
            maintenance.Candidate = null;
            maintenance.LastAttemptRound = round;
            maintenance.LastResult = result;
            if (result.Compacted)
            {
                var afterBytes = bytes - durableBytesBefore + LlmRequestTextBudget.Measure(durableHistory, llmTools);
                maintenance.ProtectionRequired = PrefixCompactionPolicy.Decide(result.AppliedAfterUsedTokens ?? measured.UsedTokens,
                    registeredWindow, afterBytes, LlmRequestTextBudget.ResolveLimit(request),
                    thresholds: thresholds, effectiveInputLimit: inputLimit).WaitForCandidate;
                // Growth is measured within an epoch, never across its checkpoint switch.
                maintenance.PreviousTokens = 0;
                maintenance.PreviousBytes = 0;
            }
            if (recovery == 0 && decision.WaitForCandidate && result.SkipReason is
                "tool_spec_changed" or "route_changed" or "after_summary_exceeds_text_budget" or "after_summary_exceeds_hard_limit") continue;
            var evidence = new Dictionary<string, string>(result.DiagnosticEvidence ?? new Dictionary<string, string>())
            {
                ["registered_context_window_tokens"] = registeredWindow.ToString(),
                ["compaction_blocking_ratio"] = thresholds.Wait.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["compaction_trigger_tokens"] = Math.Floor(registeredWindow * thresholds.Wait).ToString(),
                ["estimated_request_tokens"] = measured.UsedTokens.ToString(),
                ["compaction_pressure"] = decision.Pressure.ToString("F4", System.Globalization.CultureInfo.InvariantCulture),
                ["compaction_upload_blocking_ratio"] = thresholds.UploadWait.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            return maintenance.LastResult = result with { DiagnosticEvidence = evidence };
        }
        return maintenance.LastResult;
    }

    private Task EmitCandidateStageAsync(RuntimeDispatchRequest request, string id, string stage,
        string eventType, string reason, bool blocking)
    {
        if (eventType == SseEventTypes.ContextCompactionStarted)
            _contextCompactionService?.UpdatePreparedCompaction(request.SessionId, id, stage, !blocking);
        return _contextManager.EmitWarmPrefixCompactionLifecycleAsync(
            request.SessionId, request.WorkspaceId, eventType,
            new { compactionId = id, sessionId = request.SessionId, mode = "WarmPrefix", stage, reason,
                background = !blocking, requiresSynchronousProtection = blocking, applied = false,
                prefixUnchanged = stage != "applied", historyPreserved = true, durableCheckpoint = false },
            request.ExecutionIdentity?.TraceId, CancellationToken.None);
    }

    private async Task<WarmPrefixCompactionOutcome> GenerateWarmPrefixCandidateAsync(
        RuntimeDispatchRequest request,
        string agentInstanceId,
        List<ChatMessage> durableHistory,
        IReadOnlyList<ChatMessage> exactOutboundHistory,
        IReadOnlyList<LlmToolDefinition> llmTools,
        LlmConfig? effectiveLlmConfig,
        int round,
        CancellationToken ct,
        bool background,
        string warmPrefixCompactionId,
        Func<CancellationToken, Task<WarmPrefixCommitBoundary>> awaitBoundary,
        PrefixCompactionThresholds thresholds, bool forcePreparation)
    {
        if (_contextUsageSnapshotStore is null)
            return WarmPrefixCompactionOutcome.NotNeeded;

        var inputLimit = LlmRequestBudgetGuard.ResolveEffectiveInputLimit(effectiveLlmConfig, workUnitInputCapacity: request.UsageBudget?.MaxInputTokens);
        var windowScale = (double)(effectiveLlmConfig?.MaxContextTokens ?? inputLimit) / Math.Max(1, inputLimit);
        var trigger = forcePreparation ? .05 : Math.Min(1, thresholds.Prepare * windowScale);
        var target = Math.Min(thresholds.Prepare, thresholds.Target * windowScale);
        // F2：冻结候选时读**真实持久代际**，落库前再 CAS 一次（详见 GetCompactionGenerationAsync）。
        var sourceGeneration = await _contextManager.GetCompactionGenerationAsync(request.SessionId, ct);
        if (!WarmPrefixCompaction.TryCreatePlan(
                _contextUsageSnapshotStore,
                request.SessionId,
                durableHistory,
                exactOutboundHistory,
                llmTools,
                effectiveLlmConfig,
                trigger,
                target,
                out var plan,
                workUnitInputCapacity: request.UsageBudget?.MaxInputTokens,
                sourceGeneration: sourceGeneration,
                providerId: request.LlmProfile?.ProviderId,
                maxRequestTextBytes: LlmRequestTextBudget.ResolveLimit(request),
                textTriggerRatio: forcePreparation ? .05 : thresholds.Prepare, enforceTarget: forcePreparation)
            || plan is null)
        {
            return WarmPrefixCompactionOutcome.NotNeeded;
        }

        // 2026-10-23 微小回收守卫：软维护（历史仍能安全容纳）若只能回收极少量 token，压缩调用本身
        // 缓存命中≈0，且改写历史尾部会作废整段前缀缓存 ⇒ 代价远大于收益，直接跳过并保留原历史。
        if (WarmPrefixSoftReclaimAdmission.ShouldSkip(
                plan.RemovedTokenEstimate, plan.InitialUsedTokens, plan.RequiresSynchronousProtection, forcePreparation))
        {
            _logger.LogInformation(
                "[AgentExec:Compaction] Skip trivial soft reclaim session={Session} round={Round} plannedReclaim={PlannedReclaim} floor={Floor} usedTokens={UsedTokens}",
                request.SessionId, round + 1, plan.RemovedTokenEstimate,
                WarmPrefixSoftReclaimAdmission.ResolveFloor(plan.InitialUsedTokens), plan.InitialUsedTokens);
            _contextCompactionService?.EndPreparedCompaction(request.SessionId, warmPrefixCompactionId);
            return WarmPrefixCompactionOutcome.NotNeeded;
        }

        var warmPrefixStartedAt = DateTimeOffset.UtcNow;
        var frozenHistory = durableHistory.ToArray();

        // Token 或文本上传预算越界均同步保护；大窗口不代表大请求能安全上传。
        if (plan.RequiresTextProtection)
            _logger.LogInformation(
                "[AgentExec:Compaction] Text upload budget triggered session={Session} round={Round} textBytes={TextBytes} limitBytes={LimitBytes} usedTokens={UsedTokens} inputLimit={InputLimit}",
                request.SessionId, round + 1, plan.InitialTextBytes, plan.MaxRequestTextBytes,
                plan.InitialUsedTokens, plan.EffectiveInputLimit);
        var summaryStartedAt = warmPrefixStartedAt;
        _contextCompactionService?.UpdatePreparedCompaction(request.SessionId, warmPrefixCompactionId,
            background ? "preparing" : "waiting", background);
        await _contextManager.EmitWarmPrefixCompactionLifecycleAsync(
            request.SessionId,
            request.WorkspaceId,
            SseEventTypes.ContextCompactionStarted,
            new
            {
                compactionId = warmPrefixCompactionId,
                sessionId = request.SessionId,
                mode = "WarmPrefix",
                stage = background ? "preparing" : "waiting",
                background, prefixUnchanged = true,
                reason = plan.RequiresTextProtection ? "request_text_budget" : WarmPrefixCompactionReasons.HardProtection,
                estimatedRequestTextBytes = plan.InitialTextBytes,
                requestTextBudgetBytes = plan.MaxRequestTextBytes,
                applied = false,
                requiresSynchronousProtection = !background,
                beforeTokens = plan.InitialUsedTokens,
                afterTokens = (int?)null,
                effectiveInputLimit = plan.EffectiveInputLimit,
                hardHeadroomTokens = plan.HardHeadroomTokens,
                candidateRemovedMessages = plan.RemovedMessageCount,
                candidateRemovedTokens = plan.RemovedTokenEstimate,
                messageCount = plan.InitialMessageCount,
                round = round + 1,
                durableCheckpoint = false,
                startedAt = warmPrefixStartedAt,
            },
            request.ExecutionIdentity?.TraceId,
            CancellationToken.None);

        var retainedTextBytes = LlmRequestTextBudget.Measure(plan.RetainedMessages, llmTools);
        if (plan.SummaryAllowance is not { Tokens: >= 16 })
        {
            var reason = plan.RequiresTextProtection ? "protected_request_exceeds_text_budget" : "protected_request_exceeds_token_budget";
            await _contextManager.EmitWarmPrefixCompactionLifecycleAsync(request.SessionId, request.WorkspaceId,
                SseEventTypes.ContextCompactionFailed, new
                {
                    compactionId = warmPrefixCompactionId, sessionId = request.SessionId,
                    mode = "WarmPrefix", stage = "preflight", reason, applied = false,
                    retainedRequestTextBytes = retainedTextBytes, requestTextBudgetBytes = plan.MaxRequestTextBytes,
                    historyPreserved = true, durableCheckpoint = false,
                }, request.ExecutionIdentity?.TraceId, CancellationToken.None);
            return new(false, plan, trigger, target, summaryStartedAt,
                WarmPrefixCompactionDisposition.Failed, 0, reason,
                DiagnosticEvidence: new Dictionary<string, string>
                {
                    ["compaction_retained_text_bytes"] = retainedTextBytes.ToString(),
                    ["compaction_summary_budget_tokens"] = "0",
                    ["compaction_selected_messages"] = plan.RemovedMessageCount.ToString(),
                    ["compaction_replacement_start"] = plan.ReplacementStart.ToString(),
                });
        }

        const string summaryProviderSource = "provider_segmented";
        var summaryOutputTokens = Math.Min(checked(plan.SummaryAllowance!.Tokens * 2), effectiveLlmConfig?.MaxOutputTokens is > 0
            ? effectiveLlmConfig.MaxOutputTokens.Value : 8192);
        var summaryConfig = (effectiveLlmConfig ?? new LlmConfig()) with
        {
            MaxOutputTokens = summaryOutputTokens,
            // DeepSeek counts reasoning in max_output_tokens. Its auxiliary summarizer uses
            // content budget separately from generation headroom; disable reasoning for this call only.
            ReasoningEffort = plan.MaxRequestTextBytes > 0 ? "none" : effectiveLlmConfig?.ReasoningEffort,
        };

        var selectedSource = WarmPrefixCompaction.SelectRemovedMessages(exactOutboundHistory, plan);
        var diagnosticFile = _compactionDiagnostics is null ? null : await _compactionDiagnostics.BeginAsync(
            warmPrefixCompactionId, new
            {
                request.SessionId, request.WorkspaceId, request.ExecutionIdentity?.TraceId,
                request.ExecutionIdentity?.RunId, request.MessageId, round = round + 1, sourceGeneration,
                provider = request.LlmProfile?.ProviderId, model = summaryConfig.ModelId,
                reasoningEffort = summaryConfig.ReasoningEffort, generationMaxOutputTokens = summaryOutputTokens,
                plan.InitialTextBytes, plan.MaxRequestTextBytes, plan.InitialUsedTokens, plan.EffectiveInputLimit,
                plan.ReplacementStart, plan.RemovedMessageCount, plan.SummaryAllowance, retainedTextBytes,
                sourceRevision = WarmPrefixCompaction.ComputeRevisionHash(exactOutboundHistory),
                requestShape = CompactionDiagnosticJournal.DescribeRequest(exactOutboundHistory),
            }, selectedSource, ct);
        var diagnosticCaptureComplete = diagnosticFile is not null;
        async Task CaptureAsync(string kind, object detail)
        {
            if (_compactionDiagnostics is not null)
                diagnosticCaptureComplete &= await _compactionDiagnostics.AppendAsync(diagnosticFile, kind, detail, CancellationToken.None);
        }

        async Task<AgentExecutionLlmInvocationResult> InvokeSummaryPartAsync(
            IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition> tools, int part)
        {
            var prefix = PrefixCacheSnapshotBuilder.Build(messages, tools, PrefixChangeReasons.CompactionReplay);
            var snapshot = part == 0 ? plan.InitialSnapshot
                : _contextUsageSnapshotStore.MeasureLlmRequest(request.SessionId, messages, tools, summaryConfig.ModelId);
            // 每段单独冻结归因，摘要的报数不得参与主请求输入校准。
            // Background work must not overwrite the foreground prepared-request slot.
            var context = new RequestContextAttribution
            {
                Source = RequestContextAttributionSources.RequestPrepareFrozen,
                SessionId = request.SessionId, CapturedAtUtc = DateTimeOffset.UtcNow,
                MessageTokens = snapshot.MessageTokens, ToolDefinitionTokens = snapshot.ToolDefinitionTokens,
                SystemMessageTokens = snapshot.SystemMessageTokens, HistoryMessageTokens = snapshot.HistoryMessageTokens,
                SystemPromptTokens = snapshot.SystemPromptTokens, CompactionSummaryTokens = snapshot.CompactionSummaryTokens,
                ConversationTokens = snapshot.ConversationTokens, ToolResultTokens = snapshot.ToolResultTokens,
                ReasoningTokens = snapshot.ReasoningTokens, ToolCount = snapshot.ToolCount,
            };
            var invocationId = BuildLlmInvocationId(request, round, "compaction") + (part == 0 ? "" : $":part-{part}");
            var attemptId = invocationId + ":a0";
            _contextUsageSnapshotStore.FreezePreparedRequest(snapshot, LlmCallPurposes.Compaction, invocationId, attemptId);
            await CaptureAsync("request", new
            {
                compactionId = warmPrefixCompactionId, invocationId, attemptId, call = part,
                summaryOutputTokens, summaryConfig.ReasoningEffort,
                inputHash = WarmPrefixCompaction.ComputeRevisionHash(messages),
                input = messages.Select(m => new { role = m.Role.ToString(), m.Content,
                    images = ChatMessageMultimodalNormalizer.GetImageParts(m).Select(image => image.ArtifactId) }),
            });
            var result = await LlmInvoker.InvokeAsync(request, agentInstanceId, messages, tools, summaryConfig,
                prefix, providerInputRecoveryAlreadyAttempted: true, round, ct,
                purpose: LlmCallPurposes.Compaction, invocationId: invocationId, attemptId: attemptId);
            // 失败或最终未采用的分段仍然可能已计费，必须逐次落账。
            if (result.Usage is not null && _tokenUsageRecorder is not null)
            {
                try
                {
                    await _tokenUsageRecorder.RecordAttributedRequiredAsync(result.Usage,
                        sourceType: "agent_compaction_llm",
                        sourceId: $"{request.SessionId}:{request.ExecutionIdentity?.RunId ?? "no-run"}:{round + 1}:warm-prefix"
                            + (part == 0 ? "" : $":part-{part}"),
                        workspaceId: request.WorkspaceId, sessionId: request.SessionId,
                        providerId: request.LlmProfile?.ProviderId ?? request.LlmConfig?.Endpoint,
                        modelId: request.LlmProfile?.ModelId ?? request.LlmConfig?.ModelId,
                        attribution: BuildTokenUsageAttribution(request, round, [], context, invocationId, attemptId),
                        prefixSnapshot: prefix, occurredAtUtc: DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[AgentExec:Compaction] Summary usage recording deferred session={Session} round={Round} part={Part}",
                        request.SessionId, round + 1, part);
                }
            }
            return result;
        }

        _logger.LogInformation(
                "[AgentExec:Compaction] Large summary replay uses bounded segments session={Session} removedMessages={Removed} partTextBytes={PartBytes}",
                request.SessionId, plan.RemovedMessageCount, BoundedCompactionSummary.PartTextBytes);
        CompactionSummaryResult summaryResult;
        try
        {
            summaryResult = await BoundedCompactionSummary.GenerateAsync(selectedSource,
                (messages, part) => InvokeSummaryPartAsync(messages, [], part), ct,
                partTextBytes: Math.Min(BoundedCompactionSummary.PartTextBytes, Math.Max(8192, plan.EffectiveInputLimit - 4096)),
                allowance: plan.SummaryAllowance,
                observe: async (attempt, input, response) =>
                {
                    _logger.LogInformation(
                        "[AgentExec:CompactionAttempt] compactionId={CompactionId} traceId={TraceId} call={Call} stage={Stage} part={Part}/{Parts} depth={Depth} regeneration={Regeneration} inputBytes={InputBytes} maxOutputTokens={MaxOutputTokens} reasoningEffort={ReasoningEffort} finishReason={FinishReason} completionTokens={CompletionTokens} reasoningTokens={ReasoningTokens} summaryTokens={SummaryTokens} summaryBytes={SummaryBytes} decision={Decision} durationMs={DurationMs}",
                        warmPrefixCompactionId, request.ExecutionIdentity?.TraceId, attempt.Call, attempt.Stage,
                        attempt.Part, attempt.PartCount, attempt.Depth, attempt.Regeneration, attempt.InputTextBytes,
                        summaryOutputTokens, summaryConfig.ReasoningEffort, attempt.FinishReason,
                        attempt.CompletionTokens, attempt.ReasoningTokens, attempt.SummaryTokens, attempt.SummaryBytes,
                        attempt.Decision, attempt.DurationMs);
                    await CaptureAsync("attempt", new
                    {
                        compactionId = warmPrefixCompactionId, attempt,
                        invocationId = BuildLlmInvocationId(request, round, "compaction") + $":part-{attempt.Call}",
                        summaryOutputTokens, summaryConfig.ReasoningEffort,
                        responseContent = response?.Content,
                    });
                });
        }
        catch (Exception ex)
        {
            await CaptureAsync("interrupted", new { errorType = ex.GetType().Name, cancelled = ct.IsCancellationRequested, historyPreserved = true });
            throw;
        }
        var summaryRejectionReason = summaryResult.FailureCode is null
            ? WarmPrefixCompactionReasons.SummaryRejected : "summary_" + summaryResult.FailureCode;
        var lastSummaryAttempt = summaryResult.Attempts.LastOrDefault();

        var summaryTruncated = AgentOutputTruncationPolicy.IsTruncated(summaryResult.Response?.FinishReason);
        var summaryFailureError = summaryTruncated
            ? "Compaction summary was truncated by the provider; original history is preserved."
            : summaryResult.ExecutionError;
        var summarySucceeded = summaryResult.Success
            && !summaryTruncated
            && summaryResult.Response is not null
            && summaryResult.Response.ToolCalls is not { Count: > 0 }
            && !string.IsNullOrWhiteSpace(summaryResult.Response.Content);
        var summaryFallbackUsed = false;
        if (!summarySucceeded)
        {
            // 先识别 provider 输入上限（若文案可解析）：DeepSeek 的
            // "maximum context length is N tokens" 以前不被识别，恢复/校准逻辑永远看不到这条事实。
            // 注意：**不能**在这里调 RecordProviderInputLimitFailure —— 它是摘要请求自己的 raw，
            // 拿它校准主请求会污染出站硬门禁（ADR-095 D6：辅助调用不得抬高主请求的保守系数）。
            var providerInputLimitRejected = LlmRequestBudgetGuard.TryGetProviderMaxInputTokens(
                summaryFailureError, out var providerMaxInputTokens);
            if (providerInputLimitRejected)
            {
                _logger.LogWarning(
                    "[AgentExec:Compaction] Warm-prefix summary rejected by provider input limit session={Session} round={Round} compactionId={CompactionId} providerMaxInputTokens={ProviderMaxInputTokens}",
                    request.SessionId,
                    round + 1,
                    warmPrefixCompactionId,
                    providerMaxInputTokens);
            }

            // 只对**确定性失败**降级：provider 明确拒绝输入长度（摘要重放本身超过模型窗口），
            // 或 provider 成功但输出不可用（空内容/工具调用）。auth/限流/5xx/超时等可恢复错误
            // 必须保持 fail-closed —— 不能让一次瞬时故障用低质量摘要永久覆盖历史。
            var fallbackEligible = !summaryTruncated && (providerInputLimitRejected || summaryResult.Success);
            var fallbackSummary = fallbackEligible
                ? await TryGenerateExtractiveFallbackSummaryAsync(request, plan, exactOutboundHistory, ct)
                : null;
            if (!string.IsNullOrWhiteSpace(fallbackSummary))
            {
                summaryFallbackUsed = true;
                summarySucceeded = true;
                summaryResult = new CompactionSummaryResult(AgentExecutionLlmInvocationResult.CreateSuccess(
                    new LlmResponse(fallbackSummary, ToolCalls: null),
                    usage: null), summaryResult.Attempts, summaryResult.FailureCode);
                _logger.LogWarning(
                    "[AgentExec:Compaction] Warm-prefix provider summary failed; using local extractive fallback session={Session} round={Round} compactionId={CompactionId} fallbackTrigger={FallbackTrigger} error={Error}",
                    request.SessionId,
                    round + 1,
                    warmPrefixCompactionId,
                    providerInputLimitRejected ? "provider_input_limit" : "provider_output_unusable",
                    summaryFailureError);
            }
        }

        if (!summarySucceeded)
        {
            await CaptureAsync("outcome", new { reason = summaryRejectionReason, attempts = summaryResult.Attempts.Count,
                historyPreserved = true, applied = false, error = summaryFailureError });
            var failureDurationMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - summaryStartedAt).TotalMilliseconds);
            _logger.LogWarning(
                "[AgentExec:Compaction] Warm-prefix summary rejected; history preserved session={Session} round={Round} compactionId={CompactionId} success={Success} toolCalls={ToolCalls} fallbackAvailable={FallbackAvailable} durationMs={DurationMs} error={Error}",
                request.SessionId,
                round + 1,
                warmPrefixCompactionId,
                summaryResult.Success,
                summaryResult.Response?.ToolCalls?.Count ?? 0,
                _extractiveSummaryGenerator is not null,
                failureDurationMs,
                summaryFailureError);
            await _contextManager.EmitWarmPrefixCompactionLifecycleAsync(
                request.SessionId,
                request.WorkspaceId,
                SseEventTypes.ContextCompactionFailed,
                new
                {
                    compactionId = warmPrefixCompactionId,
                    sessionId = request.SessionId,
                    mode = "WarmPrefix",
                    stage = "failed",
                    reason = summaryRejectionReason,
                    applied = false,
                    outcome = WarmPrefixCompactionDisposition.Failed.ToString(),
                    beforeTokens = plan.InitialUsedTokens,
                    afterTokens = plan.InitialUsedTokens,
                    effectiveInputLimit = plan.EffectiveInputLimit,
                    messageCount = plan.InitialMessageCount,
                    durationMs = failureDurationMs,
                    round = round + 1,
                    durableCheckpoint = false,
                    historyPreserved = true,
                    error = summaryFailureError,
                    summaryAttempts = summaryResult.Attempts, generationMaxOutputTokens = summaryOutputTokens,
                    diagnosticFile, diagnosticCaptureComplete,
                },
                request.ExecutionIdentity?.TraceId,
                CancellationToken.None);
            return new WarmPrefixCompactionOutcome(
                false,
                plan,
                trigger,
                target,
                summaryStartedAt,
                WarmPrefixCompactionDisposition.Failed,
                failureDurationMs,
                summaryRejectionReason,
                DiagnosticEvidence: new Dictionary<string, string>
                {
                    ["compaction_diagnostic_file"] = diagnosticFile is null ? "unavailable" : "compaction/" + diagnosticFile,
                    ["compaction_capture_complete"] = diagnosticCaptureComplete.ToString().ToLowerInvariant(),
                    ["compaction_summary_calls"] = summaryResult.Attempts.Count.ToString(),
                    ["compaction_summary_last_decision"] = lastSummaryAttempt?.Decision ?? "unavailable",
                    ["compaction_summary_finish_reason"] = lastSummaryAttempt?.FinishReason ?? "unavailable",
                    ["compaction_summary_completion_tokens"] = lastSummaryAttempt?.CompletionTokens?.ToString() ?? "unavailable",
                    ["compaction_summary_reasoning_tokens"] = lastSummaryAttempt?.ReasoningTokens?.ToString() ?? "unavailable",
                    ["compaction_generation_max_output_tokens"] = summaryOutputTokens.ToString(),
                    ["compaction_id"] = warmPrefixCompactionId,
                    ["compaction_selected_messages"] = plan.RemovedMessageCount.ToString(),
                    ["compaction_replacement_start"] = plan.ReplacementStart.ToString(),
                    ["compaction_retained_text_bytes"] = retainedTextBytes.ToString(),
                    ["compaction_summary_budget_tokens"] = plan.SummaryAllowance!.Tokens.ToString(),
                    ["compaction_summary_budget_bytes"] = plan.SummaryAllowance.TextBytes.ToString(),
                });
        }

        var safeSummary = await _keyVaultService.StripAsync(
            summaryResult.Response.Content ?? string.Empty,
            ct);

        // Preparation has no access to the live list. Only the foreground can release
        // this gate; it then awaits the whole commit before dispatching another request.
        await CaptureAsync("prepared", new { prefixUnchanged = true, background });
        await EmitCandidateStageAsync(request, warmPrefixCompactionId, "prepared", SseEventTypes.ContextCompactionStarted,
            "candidate_ready_prefix_unchanged", false);
        var boundary = await awaitBoundary(ct);
        durableHistory = boundary.History;
        exactOutboundHistory = boundary.Outbound;
        llmTools = boundary.Tools;
        effectiveLlmConfig = boundary.Config;

        // ── F2 唯一应用边界 ──
        // 成 checkpoint → 严格版本/身份 CAS → 用**真实摘要**重新完整计量 → 收益与硬上限准入。
        // 追加式准入先验证整个冻结前缀，再保留新尾部并重算；任何拒绝都保持原历史。
        // ADR-095 D4：**候选校验 + 内存历史替换**放进同一个压缩提交单飞租约（本 Turn 已持执行锁，
        // 锁序为「执行锁 → 压缩锁」，不会与既有路径形成 AB-BA）；远端摘要调用在租约之外。
        // 生命周期事件在租约释放后发布：事件写入失败不得撤销已提交历史（ADR-095 D5）。
        WarmPrefixApplyVerification verification;
        WarmPrefixCompactionDisposition rejectedDisposition = WarmPrefixCompactionDisposition.NotNeeded;
        // R3：**只有持久提交成功并替换了内存**才算应用（durableCheckpoint 是权威）。
        var durableApplied = false;
        string? appliedRejectionReason = null;
        // F2：本次 checkpoint 是否真的落库（权威事实），以及未落库时的原因码。
        var durableCheckpoint = false;
        string? durableSummaryMessageId = null;
        string? durableSkipReason = null;
        var unresolvedSourceCount = 0;
        var archivedSourceCount = 0;
        IReadOnlyList<string>? sourceProblems = null;
        await using (var compactionCommitLease =
            await _contextManager.AcquireCompactionCommitLeaseAsync(request.SessionId, ct))
        {
            var currentValues = new CurrentContextValues(
                await _contextManager.GetCompactionGenerationAsync(request.SessionId, ct),
                WarmPrefixCompaction.ResolveRouteVersion(effectiveLlmConfig, request.LlmProfile?.ProviderId),
                WarmPrefixCompaction.ResolveToolSpecVersion(exactOutboundHistory, llmTools));
            var refreshedPlan = WarmPrefixCompaction.RefreshAppendOnlyPlan(_contextUsageSnapshotStore,
                request.SessionId, frozenHistory, durableHistory, exactOutboundHistory, llmTools,
                effectiveLlmConfig, plan, currentValues, out var refreshReason);
            if (refreshedPlan is not null) plan = refreshedPlan;
            verification = refreshedPlan is null
                ? WarmPrefixApplyVerification.Rejected(refreshReason, 0, false)
                : WarmPrefixCompaction.VerifyApplication(
                _contextUsageSnapshotStore,
                request.SessionId,
                durableHistory,
                llmTools,
                effectiveLlmConfig,
                plan,
                safeSummary,
                currentValues: currentValues, exactOutboundHistory: exactOutboundHistory);

            if (verification.CanApply)
            {
                // ── R3：唯一提交边界 = **先持久提交成功，再替换内存** ──
                // 旧实现先改内存再尝试落库：落库失败时内存已经是压缩后的版本，等于「内存-only 成功」，
                // 而 DB 仍是旧历史 ⇒ 重启/其它进程看到的是另一份事实。现在反过来。
                var spanResolution = _contextManager.ResolveDurableSpan(
                    request.SessionId,
                    verification.RemovedMessages ?? []);
                var sources = _contextManager.CaptureCompactionSources(
                    request.SessionId, verification.RemovedMessages ?? []);
                unresolvedSourceCount = spanResolution.UnresolvedCount;

                if (WarmPrefixDurableCommitDecision.ShouldAttempt(
                        _contextCompactionService is not null, spanResolution, sources.Count))
                {
                    try
                    {
                        var commit = await _contextCompactionService!.CommitWarmPrefixCheckpointAsync(
                            new WarmPrefixCheckpointCommitRequest(
                                SessionId: request.SessionId,
                                WorkspaceId: request.WorkspaceId,
                                AgentId: agentInstanceId,
                                CompactionId: warmPrefixCompactionId,
                                ExpectedSourceGeneration: sourceGeneration,
                                CoveredMessageIds: spanResolution.MessageIds,
                                Summary: safeSummary,
                                MessagesBefore: plan.InitialMessageCount,
                                MessagesAfter: verification.AppliedMessages!.Count,
                                TokensBefore: plan.InitialUsedTokens,
                                MeasuredTokensAfter: verification.AfterSnapshot!.UsedTokens,
                                Reason: plan.RequiresSynchronousProtection ? "hard_protection" : "soft_maintenance",
                                SourceMessages: sources),
                            ct);
                        (durableCheckpoint, durableSkipReason) =
                            WarmPrefixDurableCommitDecision.Interpret(commit);
                        durableSummaryMessageId = commit.SummaryMessageId;
                        archivedSourceCount = commit.ArchivedMessageCount;
                        sourceProblems = commit.ProblemMessageIds;
                        _logger.LogInformation(
                            "[AgentExec:Compaction] Source coverage session={Session} compactionId={CompactionId} selected={Selected} unresolved={Unresolved} archived={Archived} status={Status} problems={Problems}",
                            request.SessionId, warmPrefixCompactionId, sources.Count, unresolvedSourceCount,
                            archivedSourceCount, commit.Status, string.Join(",", sourceProblems ?? []));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // 取消：事务随 Dispose 回滚，内存尚未改动 ⇒ 两边都保持原值。
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 落库抛异常（故障注入 / DB 错误）：事务已回滚，内存尚未改动。
                        // 维护是**可选**的，不得因此中断安全消息（ADR-095 D2/D7）⇒ 降级为拒绝。
                        durableCheckpoint = false;
                        durableSkipReason = "durable_commit_failed";
                        _logger.LogError(
                            ex,
                            "[AgentExec:Compaction] Warm-prefix durable commit failed; history preserved session={Session} round={Round} compactionId={CompactionId}",
                            request.SessionId,
                            round + 1,
                            warmPrefixCompactionId);
                    }
                }
                else
                {
                    durableSkipReason = WarmPrefixDurableCommitDecision.ResolveSkipReason(
                        _contextCompactionService is not null,
                        spanResolution);
                }

                if (durableCheckpoint)
                {
                    // 持久提交已经成功（事务内 CAS + 摘要/清单/标注都落库）⇒ 现在才替换内存。
                    durableHistory.Clear();
                    durableHistory.AddRange(verification.AppliedMessages!);
                    if (!string.IsNullOrWhiteSpace(durableSummaryMessageId))
                        _contextManager.RegisterDurableCheckpoint(request.SessionId,
                            durableHistory[plan.ReplacementStart], durableSummaryMessageId);
                    durableApplied = true;
                    // 只有真的改写了历史才登记完成（冷却 + 清除无收益抑制）。
                    _contextManager.RecordCompactionCommitCompleted(request.SessionId);
                }
                else
                {
                    // 落库未成功（服务不可用 / 身份不可解析 / 代次或覆盖 CAS 冲突）：
                    // 显式保留原历史，绝不做「内存-only」应用（R3）。
                    rejectedDisposition = WarmPrefixCompactionDisposition.Failed;
                    appliedRejectionReason = durableSkipReason ?? "durable_commit_unavailable";
                }
            }
            else
            {
                rejectedDisposition = verification.Reason switch
                {
                    WarmPrefixCompactionReasons.SummaryNoGain
                        or "summary_empty" or "summary_not_smaller_than_span"
                        => WarmPrefixCompactionDisposition.NoGain,
                    "generation_changed" or "history_revision_changed" or "retained_context_changed"
                        or "selected_span_not_intact" or "route_changed" or "tool_spec_changed"
                        or "policy_changed" or "frozen_prefix_changed"
                        => WarmPrefixCompactionDisposition.StaleCandidate,
                    _ => WarmPrefixCompactionDisposition.Failed,
                };
                appliedRejectionReason = verification.Reason;
            }
        }
        var verifyDurationMs = Math.Max(0, (long)(DateTimeOffset.UtcNow - summaryStartedAt).TotalMilliseconds);

        // R3：判据是「落库 + 应用」成功，而不是「校验通过」。
        appliedRejectionReason ??= verification.Reason;
        if (!durableApplied)
        {
            await CaptureAsync("outcome", new { applied = false, reason = appliedRejectionReason, durableSkipReason,
                measuredAfterTextBytes = verification.AfterTextBytes, afterTokens = verification.AfterSnapshot?.UsedTokens,
                unresolvedSourceCount, sourceProblems, historyPreserved = true });

            _logger.LogWarning(
                "[AgentExec:Compaction] Warm-prefix apply rejected; history preserved session={Session} round={Round} compactionId={CompactionId} reason={Reason} beforeTokens={BeforeTokens} measuredAfterTokens={MeasuredAfterTokens} planEstimateExcludingSummary={PlanEstimate} reclaim={Reclaim} limit={Limit} durationMs={DurationMs} summarySource={SummarySource}",
                request.SessionId,
                round + 1,
                warmPrefixCompactionId,
                appliedRejectionReason,
                plan.InitialUsedTokens,
                verification.AfterSnapshot?.UsedTokens,
                plan.EstimatedRetainedSnapshot.UsedTokens,
                verification.ReclaimTokens,
                plan.EffectiveInputLimit,
                verifyDurationMs,
                summaryFallbackUsed ? "extractive_fallback" : summaryProviderSource);
            await _contextManager.EmitWarmPrefixCompactionLifecycleAsync(
                request.SessionId,
                request.WorkspaceId,
                SseEventTypes.ContextCompactionFailed,
                new
                {
                    compactionId = warmPrefixCompactionId,
                    sessionId = request.SessionId,
                    mode = "WarmPrefix",
                    stage = "rejected",
                    unresolvedSourceCount,
                    archivedSourceCount,
                    sourceProblems,
                    reason = appliedRejectionReason,
                    applied = false,
                    outcome = rejectedDisposition.ToString(),
                    beforeTokens = plan.InitialUsedTokens,
                    // 只有真实计量过的值才可作为 after；未计量时为 null，绝不用计划估算顶替。
                    afterTokens = verification.AfterSnapshot?.UsedTokens,
                    measuredAfterTextBytes = verification.AfterTextBytes,
                    summaryBudgetTokens = plan.SummaryAllowance?.Tokens,
                    summaryBudgetTextBytes = plan.SummaryAllowance?.TextBytes,
                    replacementStart = plan.ReplacementStart,
                    planEstimateExcludingSummaryTokens = plan.EstimatedRetainedSnapshot.UsedTokens,
                    reclaimTokens = verification.ReclaimTokens,
                    effectiveInputLimit = plan.EffectiveInputLimit,
                    candidateRemovedTokens = plan.RemovedTokenEstimate,
                    summaryTokens = ContextUsageSnapshotStore.CountTokens(safeSummary),
                    messageCount = plan.InitialMessageCount,
                    durationMs = verifyDurationMs,
                    round = round + 1,
                    durableCheckpoint = false,
                    historyPreserved = true,
                    // 兜底摘要已生成但应用/持久提交被拒时，保留 provider 失败事实，避免诊断信息倒退。
                    summarySource = summaryFallbackUsed ? "extractive_fallback" : null,
                    summaryFallbackError = summaryFallbackUsed ? summaryFailureError : null,
                },
                request.ExecutionIdentity?.TraceId,
                CancellationToken.None);
            return new WarmPrefixCompactionOutcome(
                false,
                plan,
                trigger,
                target,
                summaryStartedAt,
                rejectedDisposition,
                verifyDurationMs,
                appliedRejectionReason,
                DiagnosticEvidence: new Dictionary<string, string>
                {
                    ["compaction_id"] = warmPrefixCompactionId,
                    ["compaction_selected_messages"] = plan.RemovedMessageCount.ToString(),
                    ["compaction_diagnostic_file"] = diagnosticFile is null ? "unavailable" : "compaction/" + diagnosticFile,
                    ["compaction_capture_complete"] = diagnosticCaptureComplete.ToString().ToLowerInvariant(),
                    ["compaction_unresolved_sources"] = unresolvedSourceCount.ToString(),
                    ["compaction_source_problems"] = string.Join(",", (sourceProblems ?? []).Take(8)),
                    ["compaction_measured_after_bytes"] = verification.AfterTextBytes?.ToString() ?? "unavailable",
                    ["compaction_summary_budget_tokens"] = plan.SummaryAllowance?.Tokens.ToString() ?? "unavailable",
                    ["compaction_summary_budget_bytes"] = plan.SummaryAllowance?.TextBytes.ToString() ?? "unavailable",
                    ["compaction_summary_actual_bytes"] = LlmRequestTextBudget.MeasureSummary(safeSummary).ToString(),
                    ["compaction_retained_text_bytes"] = retainedTextBytes.ToString(),
                    ["compaction_replacement_start"] = plan.ReplacementStart.ToString(),
                });
        }

        await CaptureAsync("outcome", new { applied = true, durableCheckpoint, durableSummaryMessageId,
            measuredAfterTextBytes = verification.AfterTextBytes, afterTokens = verification.AfterSnapshot?.UsedTokens,
            summaryContent = safeSummary, attempts = summaryResult.Attempts.Count,
            requestShape = CompactionDiagnosticJournal.DescribeRequest(durableHistory) });
        // 校验通过：内存历史已在临界区内替换完毕，这里只做日志与生命周期事件。
        var afterUsedTokens = verification.AfterSnapshot!.UsedTokens;
        var appliedDurationMs = verifyDurationMs;
        _logger.LogInformation(
            "[AgentExec:Compaction] Warm-prefix checkpoint committed session={Session} round={Round} compactionId={CompactionId} removed={Removed} messages={Before}->{After} measuredBefore={BeforeTokens} measuredAfter={AfterTokens} planEstimateExcludingSummary={PlanEstimate} reclaim={Reclaim} meetsTarget={MeetsTarget} durationMs={DurationMs} durableCheckpoint={DurableCheckpoint} durableSkipReason={DurableSkipReason} summarySource={SummarySource}",
            request.SessionId,
            round + 1,
            warmPrefixCompactionId,
            plan.RemovedMessageCount,
            plan.InitialMessageCount,
            durableHistory.Count,
            plan.InitialUsedTokens,
            afterUsedTokens,
            plan.EstimatedRetainedSnapshot.UsedTokens,
            verification.ReclaimTokens,
            verification.MeetsTarget,
            appliedDurationMs,
            durableCheckpoint,
            durableSkipReason,
            summaryFallbackUsed ? "extractive_fallback" : summaryProviderSource);
        await _contextManager.EmitWarmPrefixCompactionLifecycleAsync(
            request.SessionId,
            request.WorkspaceId,
            SseEventTypes.ContextCompactionCompleted,
            new
            {
                compactionId = warmPrefixCompactionId,
                sessionId = request.SessionId,
                mode = "WarmPrefix",
                stage = "applied",
                reason = plan.RequiresTextProtection ? "request_text_budget" : WarmPrefixCompactionReasons.HardProtection,
                estimatedRequestTextBytesBefore = plan.InitialTextBytes,
                estimatedRequestTextBytesAfter = LlmRequestTextBudget.Measure(durableHistory, llmTools),
                requestTextBudgetBytes = plan.MaxRequestTextBytes,
                // 只有 applied=true 才允许前端显示"已压缩"（ADR-095 D7）。
                applied = true,
                outcome = WarmPrefixCompactionDisposition.Applied.ToString(),
                beforeTokens = plan.InitialUsedTokens,
                // 加入真实摘要后**重新计量**的值；不是 EstimatedRetainedSnapshot。
                afterTokens = afterUsedTokens,
                planEstimateExcludingSummaryTokens = plan.EstimatedRetainedSnapshot.UsedTokens,
                reclaimedTokens = verification.ReclaimTokens,
                meetsTarget = verification.MeetsTarget,
                effectiveInputLimit = plan.EffectiveInputLimit,
                removedMessages = plan.RemovedMessageCount,
                candidateRemovedTokens = plan.RemovedTokenEstimate,
                messageCountBefore = plan.InitialMessageCount,
                messageCountAfter = durableHistory.Count,
                durationMs = appliedDurationMs,
                round = round + 1,
                // F2：权威事实。false 表示只改了内存（DB 仍是完整历史），并给出原因码；
                // 前端不得把 durableCheckpoint=false 当作"已持久压缩"（ADR-095 D7）。
                durableCheckpoint,
                durableSkipReason,
                // provider 摘要失败后改用本地抽取式摘要时如实标注（不是失败，但也不是原样重放摘要）；
                // `degraded=true` 供 UI/诊断在不改变卡片语义前先能读到事实。
                summarySource = summaryFallbackUsed ? "extractive_fallback" : summaryProviderSource,
                degraded = summaryFallbackUsed,
                summaryAttempts = summaryResult.Attempts, diagnosticFile, diagnosticCaptureComplete,
            },
            request.ExecutionIdentity?.TraceId,
            CancellationToken.None);
        return new WarmPrefixCompactionOutcome(
            true,
            plan,
            trigger,
            target,
            summaryStartedAt,
            WarmPrefixCompactionDisposition.Applied,
            appliedDurationMs,
            SkipReason: null,
            // 只有真实计量过的 after 才允许出口；调用方的日志/事件/子代理事件都必须用它。
            AppliedAfterUsedTokens: afterUsedTokens);
    }

}
