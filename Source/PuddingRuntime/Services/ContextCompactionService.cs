using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;

namespace PuddingRuntime.Services;

public sealed class ContextCompactionService : IContextCompactionService
{
    private const int RecentMessagesToKeep = 6;
    private const int MinCompactionInputMessages = 20;
    private const int MaxCompactionInputMessages = 80;
    private const int MaxActiveMessagesToLoad = 500;
    private const int MaxHealthEstimateSampleSize = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDbContextFactory<MemoryDbContext> _dbFactory;
    private readonly IContextCompactionSummaryGenerator _summaryGenerator;
    private readonly ILogger<ContextCompactionService> _logger;
    private readonly IAgentContentSummaryService? _contentSummaryService;
    private readonly ITokenUsageEventRepository? _tokenUsageRepo;
    private readonly ICompactionChatMessageStore? _messageStore;
    private readonly SessionSummaryStore? _sessionSummaryStore;
    private readonly ContextUsageSnapshotStore? _contextUsageSnapshotStore;
    private readonly ContextCompactionOptions? _options;
    private readonly IHookPublisher? _hookPublisher;
    private readonly PuddingDataPaths? _dataPaths;

    public ContextCompactionService(
        IDbContextFactory<MemoryDbContext> dbFactory,
        IContextCompactionSummaryGenerator summaryGenerator,
        ILogger<ContextCompactionService> logger,
        IAgentContentSummaryService? contentSummaryService = null,
        ITokenUsageEventRepository? tokenUsageRepo = null,
        ICompactionChatMessageStore? messageStore = null,
        SessionSummaryStore? sessionSummaryStore = null,
        ContextUsageSnapshotStore? contextUsageSnapshotStore = null,
        ContextCompactionOptions? options = null,
        IHookPublisher? hookPublisher = null,
        PuddingDataPaths? dataPaths = null)
    {
        _dbFactory = dbFactory;
        _summaryGenerator = summaryGenerator;
        _logger = logger;
        _contentSummaryService = contentSummaryService;
        _tokenUsageRepo = tokenUsageRepo;
        _messageStore = messageStore;
        _sessionSummaryStore = sessionSummaryStore;
        _contextUsageSnapshotStore = contextUsageSnapshotStore;
        _options = options;
        _hookPublisher = hookPublisher;
        _dataPaths = dataPaths;
    }

    public async Task<ContextHealthSnapshot> GetHealthAsync(
        string sessionId,
        CancellationToken ct = default,
        int? contextWindowTokens = null,
        int? maxOutputTokens = null,
        int? maxInputTokens = null,
        int toolCount = 0)
    {
        if (contextWindowTokens is not > 0)
            throw new InvalidOperationException(
                $"Context window tokens were not resolved for session '{sessionId}'.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var usage = await ResolveCurrentContextUsageAsync(db, sessionId, ct);

                var threshold = _options?.AutoCompactionThreshold is > 0 and <= 1
            ? _options.AutoCompactionThreshold
            : 0.65;
        return new ContextHealthEvaluator().Evaluate(
            sessionId,
            usage.UsedTokens,
            contextWindowTokens: contextWindowTokens.Value,
            maxOutputTokens: maxOutputTokens ?? 2_048,
            maxInputTokens: maxInputTokens,
            compactionThreshold: threshold) with
        {
            UsageSource = usage.Source,
            UsageConfidence = usage.Confidence,
            UsageRecordedAtUtc = usage.RecordedAt == default ? null : usage.RecordedAt.ToString("O"),
            MessageTokens = usage.MessageTokens,
            ToolDefinitionTokens = usage.ToolDefinitionTokens,
            SystemMessageTokens = usage.SystemMessageTokens,
            HistoryMessageTokens = usage.HistoryMessageTokens,
            MessageCount = usage.MessageCount,
            ToolCount = usage.ToolCount,
            ProviderPromptTokens = usage.ProviderPromptTokens,
            ProviderCompletionTokens = usage.ProviderCompletionTokens,
            ProviderTotalTokens = usage.ProviderTotalTokens,
        };
    }

    private async Task<ContextUsageSnapshot> ResolveCurrentContextUsageAsync(
        MemoryDbContext db,
        string sessionId,
        CancellationToken ct)
    {
        ContextUsageSnapshot? snapshot = null;
        if (_contextUsageSnapshotStore is not null)
            _contextUsageSnapshotStore.TryGet(sessionId, out snapshot);

        if (snapshot?.UsedTokens > 0
            && string.Equals(snapshot.Source, "provider_usage", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot;
        }

        var latestUsage = await TryGetLatestProviderUsageAsync(sessionId, ct);
        if (latestUsage is not null)
            return latestUsage;

        if (snapshot?.UsedTokens > 0)
            return snapshot;

        var activeTokens = await EstimateActiveTokensAsync(db, sessionId, ct);
        if (activeTokens > 0)
        {
            return new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = activeTokens,
                MessageTokens = activeTokens,
                HistoryMessageTokens = activeTokens,
                Source = "active_session_messages",
                Confidence = "estimated",
            };
        }

        var transcriptEstimate = await TryEstimateCanonicalTranscriptAsync(sessionId, ct);
        if (transcriptEstimate is not null)
            return transcriptEstimate;

        return new ContextUsageSnapshot
        {
            SessionId = sessionId,
            RecordedAt = DateTimeOffset.UtcNow,
            UsedTokens = 0,
            MessageTokens = 0,
            HistoryMessageTokens = 0,
            Source = "active_session_messages",
            Confidence = "estimated",
        };
    }

    private async Task<ContextUsageSnapshot?> TryEstimateCanonicalTranscriptAsync(
        string sessionId,
        CancellationToken ct)
    {
        if (_messageStore is null)
            return null;

        try
        {
            var messages = await _messageStore.GetRecentForSessionAsync(
                sessionId,
                MaxActiveMessagesToLoad,
                ct);
            if (messages.Count == 0)
                return null;

            var estimatedTokens = Math.Max(
                1,
                messages.Sum(message => ContextUsageSnapshotStore.CountTokens(message.Content)));
            return new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = DateTimeOffset.UtcNow,
                UsedTokens = estimatedTokens,
                MessageTokens = estimatedTokens,
                HistoryMessageTokens = estimatedTokens,
                MessageCount = messages.Count,
                Source = "canonical_chat_transcript",
                Confidence = "estimated",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[ContextCompaction] Failed to estimate canonical transcript session={SessionId}",
                sessionId);
            return null;
        }
    }

    private async Task<ContextUsageSnapshot?> TryGetLatestProviderUsageAsync(string sessionId, CancellationToken ct)
    {
        if (_tokenUsageRepo is null)
            return null;

        try
        {
            var latest = await _tokenUsageRepo.GetLatestStatsAsync(sessionId, ct);

            if (latest is null)
                return null;

            var usedTokens = latest.TotalTokens > 0
                ? latest.TotalTokens
                : latest.PromptTokens;
            return new ContextUsageSnapshot
            {
                SessionId = sessionId,
                RecordedAt = latest.OccurredAtUtc,
                UsedTokens = usedTokens > int.MaxValue ? int.MaxValue : (int)Math.Max(0, usedTokens),
                Source = "provider_usage_db",
                Confidence = "provider_reported",
                ProviderPromptTokens = latest.PromptTokens > int.MaxValue ? int.MaxValue : (int?)latest.PromptTokens,
                ProviderCompletionTokens = latest.CompletionTokens > int.MaxValue ? int.MaxValue : (int?)latest.CompletionTokens,
                ProviderTotalTokens = latest.TotalTokens > int.MaxValue ? int.MaxValue : (int?)latest.TotalTokens,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[ContextCompaction] Failed to load latest prompt tokens session={SessionId}; falling back to local estimate",
                sessionId);
            return null;
        }
    }

    public async Task<ContextCompactionResult> CompactAsync(
        ContextCompactionRequest request,
        CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var compactionId = string.IsNullOrWhiteSpace(request.CompactionId)
            ? Guid.NewGuid().ToString("N")
            : request.CompactionId.Trim();
        var startedAtUtc = DateTimeOffset.UtcNow;
        if (request.Level != ContextCompactionLevel.Full)
            throw new NotSupportedException($"Context compaction level '{request.Level}' is not implemented yet.");

        _logger.LogInformation(
            "[ContextCompaction:Phase] start compactionId={CompactionId} session={SessionId} mode={Mode} reason={Reason}",
            compactionId, request.SessionId, request.Mode, request.Reason);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var activeMessages = await LoadActiveMessagesAsync(db, request.SessionId, ct);
        var loadMs = sw.ElapsedMilliseconds;
        _logger.LogInformation(
            "[ContextCompaction:Phase] loadActive session={SessionId} count={Count} elapsedMs={ElapsedMs}",
            request.SessionId, activeMessages.Count, loadMs);

        if (activeMessages.Count == 0)
        {
            _logger.LogInformation(
                "[ContextCompaction:Phase] importTranscript session={SessionId}",
                request.SessionId);
            await TryImportCurrentSessionTranscriptAsync(db, request, ct);
            activeMessages = await LoadActiveMessagesAsync(db, request.SessionId, ct);
            _logger.LogInformation(
                "[ContextCompaction:Phase] afterImport session={SessionId} count={Count}",
                request.SessionId, activeMessages.Count);
        }

        var candidates = activeMessages
            .Where(m => string.Equals(m.ContentType, "text", StringComparison.OrdinalIgnoreCase))
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Sequence)
            .ToList();

        var messagesToCompact = candidates
            .Take(Math.Max(0, candidates.Count - RecentMessagesToKeep))
            .ToList();

        _logger.LogInformation(
            "[ContextCompaction:Phase] filter session={SessionId} candidates={Candidates} toCompact={ToCompact} keepRecent={KeepRecent}",
            request.SessionId, candidates.Count, messagesToCompact.Count, RecentMessagesToKeep);

        if (candidates.Count == 0)
        {
            sw.Stop();
            var noOpBeforeTokens = EstimateMessages(activeMessages);
            var noOpDiagnostics = BuildDiagnostics(
                request,
                compactionId,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                sw.ElapsedMilliseconds,
                activeMessages,
                candidates,
                messagesToCompact,
                summaryInputMessages: [],
                summaryMessageId: string.Empty,
                beforeTokens: noOpBeforeTokens,
                afterTokens: noOpBeforeTokens,
                summary: string.Empty,
                summaryGenerator: ResolveSummaryGeneratorName());
            _logger.LogInformation(
                "[ContextCompaction:Phase] skipNoOp compactionId={CompactionId} session={SessionId} elapsedMs={ElapsedMs}",
                compactionId, request.SessionId, sw.ElapsedMilliseconds);
                        var noOpResult = new ContextCompactionResult(
                request.SessionId,
                SummaryMessageId: string.Empty,
                request.Mode,
                request.Level,
                BeforeTokens: noOpBeforeTokens,
                AfterTokens: noOpBeforeTokens,
                CompactedMessageCount: 0,
                SummaryPreview: string.Empty,
                SummaryMarkdown: string.Empty,
                MemoryNotes: [],
                Diagnostics: noOpDiagnostics);
            await WriteCompactionLogAsync(request, noOpResult, ct);
            return noOpResult;
        }

        var expandStart = sw.ElapsedMilliseconds;
        var summaryBaseMessages = messagesToCompact.Count > 0
            ? messagesToCompact
            : candidates;
        var expandedInput = await ExpandToMinimumInputAsync(db, request.SessionId, summaryBaseMessages, ct);
        _logger.LogInformation(
            "[ContextCompaction:Phase] expandInput session={SessionId} baseCount={BaseCount} expandedCount={ExpandedCount} supBefore={SuppBefore} elapsedMs={ElapsedMs}",
            request.SessionId, summaryBaseMessages.Count, expandedInput.Messages.Count, expandedInput.SupplementalBeforeCount, sw.ElapsedMilliseconds - expandStart);

        var windowStart = sw.ElapsedMilliseconds;
        var summaryInput = SelectSummaryInputWindow(expandedInput);
        _logger.LogInformation(
            "[ContextCompaction:Phase] selectWindow session={SessionId} windowCount={WindowCount} firstSeq={FirstSeq} lastSeq={LastSeq} omitted={Omitted}",
            request.SessionId, summaryInput.Messages.Count, summaryInput.FirstIncludedSequence, summaryInput.LastIncludedSequence, summaryInput.OmittedBeforeCount);

                var summaryStart = sw.ElapsedMilliseconds;
        
        // 如果有 Agent 工作总结，记录日志
                if (!string.IsNullOrWhiteSpace(request.AgentWorkSummary))
        {
            var wsPreview = request.AgentWorkSummary.Replace("\r", " ").Replace("\n", " ").Trim();
            if (wsPreview.Length > 120) wsPreview = wsPreview[..120] + "…";
            _logger.LogInformation(
                "[ContextCompaction:Phase] agentWorkSummary provided session={SessionId} len={Len} preview={Preview}",
                request.SessionId, request.AgentWorkSummary.Length, wsPreview);
        }
        
        var summary = await _summaryGenerator.GenerateSummaryAsync(
            new ContextCompactionSummaryRequest(
                request.WorkspaceId,
                request.SessionId,
                request.AgentId,
                summaryInput.Messages
                    .Select(m => new ContextCompactionMessage(
                        m.MessageId,
                        m.Sequence,
                        m.Role,
                        m.Content ?? string.Empty))
                    .ToList(),
                request.Reason,
                AgentWorkSummary: request.AgentWorkSummary,
                AgentTemplateId: request.AgentTemplateId,
                UserId: request.UserId,
                LlmConfig: request.LlmConfig,
                CapabilityPolicy: request.CapabilityPolicy,
                ToolDefinitions: request.ToolDefinitions,
                SkillPackages: request.SkillPackages),
            ct);

        if (string.IsNullOrWhiteSpace(summary))
            throw new InvalidOperationException("Context compaction summary generator returned an empty summary.");
        summary = ApplySummaryInputNotice(summary, summaryInput);
        var summaryMs = sw.ElapsedMilliseconds - summaryStart;
        _logger.LogInformation(
            "[ContextCompaction:Phase] generateSummary session={SessionId} summaryLen={SummaryLen} elapsedMs={ElapsedMs}",
            request.SessionId, summary.Length, summaryMs);

                // SkipWhenSummaryIncreasesTokens: 摘要 token 数 ≥ 被压缩内容 token 数时跳过本次压缩
        var skipDueToTokenIncrease = false;
        if (_options is not null && _options.SkipWhenSummaryIncreasesTokens)
        {
            var summaryTokens = ContextUsageSnapshotStore.CountTokens(summary);
            var inputTokens = summaryInput.Messages.Sum(m => ContextUsageSnapshotStore.CountTokens(m.Content ?? string.Empty));
            if (summaryTokens >= inputTokens)
            {
                _logger.LogWarning(
                    "[ContextCompaction] Skipping compaction summary increases tokens compactionId={CompactionId} session={SessionId} summaryTokens={SummaryTokens} inputTokens={InputTokens}",
                    compactionId, request.SessionId, summaryTokens, inputTokens);

                // 向 compaction-log.jsonl 追加跳过记录
                try
                {
                    var logDir = _dataPaths is not null
                        ? _dataPaths.DiagnosticsLogsRoot
                        : Path.Combine(Directory.GetCurrentDirectory(), "data");
                    Directory.CreateDirectory(logDir);
                    var logPath = Path.Combine(logDir, "compaction-log.jsonl");
                    var logLine = JsonSerializer.Serialize(new
                    {
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                        compactionId,
                        sessionId = request.SessionId,
                        workspaceId = request.WorkspaceId,
                        agentId = request.AgentId,
                        mode = request.Mode.ToString(),
                        reason = "summary_increases_tokens",
                        beforeTokens = inputTokens,
                        afterTokens = summaryTokens,
                        summaryTokens,
                        inputTokens,
                        compactedMessageCount = 0,
                        durationMs = sw.ElapsedMilliseconds,
                        summaryGenerator = ResolveSummaryGeneratorName(),
                    }, JsonOptions);
                    await File.AppendAllTextAsync(logPath, logLine + Environment.NewLine, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[ContextCompaction] Failed to write skip log session={SessionId}",
                        request.SessionId);
                }

                skipDueToTokenIncrease = true;
            }
        }

        if (skipDueToTokenIncrease)
        {
            sw.Stop();
            var skipResult = new ContextCompactionResult(
                request.SessionId, string.Empty, request.Mode, request.Level,
                EstimateMessages(activeMessages), EstimateMessages(activeMessages),
                0, string.Empty, string.Empty, [], null, SkippedDueToTokenIncrease: true);
            return skipResult;
        }

        summary = AppendKeyFactsSection(summary, request.PreCompactionFacts);

        var beforeTokens = EstimateMessages(activeMessages);
        var summaryMessage = new MessageEntity
        {
            MessageId = Guid.NewGuid().ToString("N"),
            SessionId = request.SessionId,
            Sequence = activeMessages.Max(m => m.Sequence) + 1,
            Role = "system",
            ContentType = "compact_summary",
            Content = summary,
            Source = "context_compaction",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Metadata = JsonSerializer.Serialize(new
            {
                compactionId,
                mode = request.Mode.ToString(),
                level = request.Level.ToString(),
                reason = request.Reason,
                compactedMessageCount = messagesToCompact.Count,
                summaryInputMessageCount = summaryInput.Messages.Count,
                summaryInputFirstSequence = summaryInput.FirstIncludedSequence,
                summaryInputLastSequence = summaryInput.LastIncludedSequence,
                supplementalBeforeSummaryInputCount = summaryInput.SupplementalBeforeCount,
                supplementalBeforeSummaryInputFirstSequence = summaryInput.FirstSupplementalSequence,
                supplementalBeforeSummaryInputLastSequence = summaryInput.LastSupplementalSequence,
                omittedBeforeSummaryInputCount = summaryInput.OmittedBeforeCount,
                omittedBeforeSummaryInputFirstSequence = summaryInput.FirstOmittedSequence,
                omittedBeforeSummaryInputLastSequence = summaryInput.LastOmittedSequence,
                summaryGenerator = ResolveSummaryGeneratorName(),
                beforeTokens,
            }, JsonOptions),
        };

        db.Messages.Add(summaryMessage);
        foreach (var message in messagesToCompact)
            message.CompactedBy = summaryMessage.MessageId;

        await db.SaveChangesAsync(ct);

        var afterMessages = await LoadActiveMessagesAsync(db, request.SessionId, ct);
        var afterTokens = EstimateMessages(afterMessages);
        await SaveAgentContentSummaryAsync(request, summary, ct);

        // 写入 SessionSummaryStore，让新 Session 的 HISTORICAL-CONTEXT 层可以召回本次压缩摘要
        if (_sessionSummaryStore is not null && !string.IsNullOrWhiteSpace(request.AgentId))
        {
            try
            {
                var recentForSummary = activeMessages
                    .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                    .TakeLast(13)
                    .Select(m => $"- [{m.Role}]: {TruncateContent(m.Content ?? string.Empty, 200)}")
                    .ToList();

                _ = _sessionSummaryStore.SaveAsync(
                    request.AgentId,
                    request.SessionId,
                    summary,
                    recentForSummary,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[ContextCompaction] SessionSummaryStore save failed session={SessionId}",
                    request.SessionId);
            }
        }

                var compressionRatio = beforeTokens > 0
            ? (double)(beforeTokens - afterTokens) / beforeTokens
            : 0.0;

        _logger.LogInformation(
            "[ContextCompaction] Full compact completed compactionId={CompactionId} session={SessionId} compacted={Count} before={BeforeTokens} after={AfterTokens} ratio={CompressionRatio:P1} summaryLen={SummaryLen}",
            compactionId, request.SessionId, messagesToCompact.Count, beforeTokens, afterTokens,
            compressionRatio, summary.Length);

        var completedAtUtc = DateTimeOffset.UtcNow;
        var diagnostics = BuildDiagnostics(
            request,
            compactionId,
            startedAtUtc,
            completedAtUtc,
            sw.ElapsedMilliseconds,
            activeMessages,
            candidates,
            messagesToCompact,
            summaryInput.Messages,
            summaryMessage.MessageId,
            beforeTokens,
            afterTokens,
            summary,
            ResolveSummaryGeneratorName());

        var memoryNotes = MergeMemoryNotes(request.PreCompactionFacts, ExtractMemoryNotes(summary));
        var result = new ContextCompactionResult(
            request.SessionId,
            summaryMessage.MessageId,
            request.Mode,
            request.Level,
            beforeTokens,
            afterTokens,
            messagesToCompact.Count,
            BuildPreview(summary),
            summary,
            memoryNotes,
            diagnostics);

                await PublishSessionCompressedHookAsync(request, result, ct);
        await WriteCompactionLogAsync(request, result, ct);
        return result;
    }

    private async Task PublishSessionCompressedHookAsync(
        ContextCompactionRequest request,
        ContextCompactionResult result,
        CancellationToken ct)
    {
        if (_hookPublisher is null || result.Diagnostics is null)
            return;

        var diagnostics = result.Diagnostics;
        try
        {
            await _hookPublisher.PublishAsync(
                HookEventNames.SessionCompressed,
                new SessionCompressedHookPayload
                {
                    WorkspaceId = diagnostics.WorkspaceId,
                    OriginalSessionId = diagnostics.PreviousSessionId,
                    NewSessionId = diagnostics.NewSessionId,
                    AgentId = diagnostics.AgentId,
                    AgentTemplateId = request.AgentTemplateId,
                    CompactionId = diagnostics.CompactionId,
                    Mode = request.Mode.ToString(),
                    Level = request.Level.ToString(),
                    Reason = diagnostics.Reason,
                    OriginalMessageCount = diagnostics.ActiveMessageCountBefore,
                    PreservedMessageCount = diagnostics.KeptRecentMessageCount,
                    DroppedMessageCount = diagnostics.CompactedMessageCount,
                    SummaryPreview = result.SummaryPreview,
                    MemoryNotes = result.MemoryNotes ?? [],
                },
                new HookPublishOptions
                {
                    WorkspaceId = diagnostics.WorkspaceId,
                    SessionId = diagnostics.PreviousSessionId,
                    AgentId = diagnostics.AgentId,
                    SourceId = "context_compaction",
                    IdempotencyKey = $"context_compaction:{diagnostics.CompactionId}",
                },
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextCompaction] Failed to publish session.compressed hook compactionId={CompactionId} session={SessionId}",
                diagnostics.CompactionId,
                diagnostics.PreviousSessionId);
        }
    }

    private async Task WriteCompactionLogAsync(
        ContextCompactionRequest request,
        ContextCompactionResult result,
        CancellationToken ct)
    {
        try
        {
            var logDir = _dataPaths is not null
                ? _dataPaths.DiagnosticsLogsRoot
                : Path.Combine(Directory.GetCurrentDirectory(), "data");
            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "compaction-log.jsonl");

            var logLine = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
                compactionId = result.Diagnostics?.CompactionId ?? "",
                sessionId = request.SessionId,
                workspaceId = request.WorkspaceId,
                agentId = request.AgentId,
                mode = request.Mode.ToString(),
                reason = request.Reason,
                beforeTokens = result.BeforeTokens,
                afterTokens = result.AfterTokens,
                compactionRatio = result.BeforeTokens > 0
                    ? Math.Round((double)result.AfterTokens / result.BeforeTokens, 3)
                    : 0,
                compactedMessageCount = result.CompactedMessageCount,
                durationMs = result.Diagnostics?.DurationMs ?? 0,
                summaryGenerator = result.Diagnostics?.SummaryGenerator ?? "",
            }, JsonOptions);

            await File.AppendAllTextAsync(logPath, logLine + Environment.NewLine, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[ContextCompaction] Failed to write compaction log session={SessionId}",
                request.SessionId);
        }
    }

    private string ResolveSummaryGeneratorName()
    {
        var configured = _options?.SummaryGenerator;
        return string.IsNullOrWhiteSpace(configured)
            ? _summaryGenerator.GetType().Name
            : configured.Trim();
    }

    private static ContextCompactionDiagnostics BuildDiagnostics(
        ContextCompactionRequest request,
        string compactionId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        long durationMs,
        IReadOnlyList<MessageEntity> activeMessages,
        IReadOnlyList<MessageEntity> candidates,
        IReadOnlyList<MessageEntity> messagesToCompact,
        IReadOnlyList<MessageEntity> summaryInputMessages,
        string summaryMessageId,
        int beforeTokens,
        int afterTokens,
        string summary,
        string summaryGenerator)
    {
        var orderedActive = activeMessages.OrderBy(m => m.Sequence).ToList();
        var lastMessage = orderedActive.LastOrDefault();
        var orderedInput = summaryInputMessages.OrderBy(m => m.Sequence).ToList();
        var firstInput = orderedInput.FirstOrDefault();
        var lastInput = orderedInput.LastOrDefault();

        return new ContextCompactionDiagnostics(
            compactionId,
            request.WorkspaceId,
            request.AgentId,
            request.SessionId,
            NewSessionId: null,
            NewSessionTitle: null,
            PreviousLastMessageId: lastMessage?.MessageId,
            PreviousLastMessageSequence: lastMessage?.Sequence,
            ActiveMessageCountBefore: activeMessages.Count,
            TextCandidateMessageCount: candidates.Count,
            CompactedMessageCount: messagesToCompact.Count,
            KeptRecentMessageCount: Math.Max(0, candidates.Count - messagesToCompact.Count),
            SummaryInputMessageCount: orderedInput.Count,
            SummaryInputFirstSequence: firstInput?.Sequence,
            SummaryInputLastSequence: lastInput?.Sequence,
            SummaryInputFirstMessageId: firstInput?.MessageId,
            SummaryInputLastMessageId: lastInput?.MessageId,
            summaryMessageId,
            beforeTokens,
            afterTokens,
            SummaryCharacterCount: summary.Length,
            SummaryEstimatedTokens: ContextUsageSnapshotStore.CountTokens(summary),
            StartedAtUtc: startedAtUtc.ToString("O"),
            CompletedAtUtc: completedAtUtc.ToString("O"),
            durationMs,
            summaryGenerator,
            request.Reason);
    }

    private async Task SaveAgentContentSummaryAsync(
        ContextCompactionRequest request,
        string summary,
        CancellationToken ct)
    {
        if (_contentSummaryService is null
            || string.IsNullOrWhiteSpace(request.AgentId)
            || string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        try
        {
            await _contentSummaryService.SaveCompressedSummaryAsync(
                new AgentCompressedContentSummaryRequest(
                    request.WorkspaceId,
                    request.AgentId,
                    AgentTemplateId: null,
                    request.SessionId,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd"),
                    summary,
                    request.Reason),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[ContextCompaction] Save agent content summary failed session={SessionId} agent={AgentId}",
                request.SessionId,
                request.AgentId);
        }
    }

    private async Task TryImportCurrentSessionTranscriptAsync(
        MemoryDbContext memoryDb,
        ContextCompactionRequest request,
        CancellationToken ct)
    {
        if (_messageStore is null)
            return;

        var transcriptRows = await _messageStore.GetAllForSessionAsync(request.SessionId, ct);

        if (transcriptRows.Count == 0)
            return;

        var session = await memoryDb.Sessions
            .FirstOrDefaultAsync(s => s.SessionId == request.SessionId, ct);
        var firstRow = transcriptRows[0];
        var lastActivityAt = transcriptRows.Max(m => m.CreatedAt);
        var agentId = string.IsNullOrWhiteSpace(request.AgentId)
            ? firstRow.AgentInstanceId
            : request.AgentId;

        if (session is null)
        {
            session = new SessionEntity
            {
                SessionId = request.SessionId,
                WorkspaceId = request.WorkspaceId,
                AgentId = agentId ?? string.Empty,
                Status = "active",
                CreatedAt = firstRow.CreatedAt,
                LastActivityAt = lastActivityAt,
            };
            memoryDb.Sessions.Add(session);
        }
        else
        {
            session.WorkspaceId = string.IsNullOrWhiteSpace(session.WorkspaceId)
                ? request.WorkspaceId
                : session.WorkspaceId;
            session.AgentId = string.IsNullOrWhiteSpace(session.AgentId)
                ? agentId ?? string.Empty
                : session.AgentId;
            session.LastActivityAt = Math.Max(session.LastActivityAt, lastActivityAt);
        }

        var existingMessageIds = await memoryDb.Messages
            .Where(m => m.SessionId == request.SessionId)
            .Select(m => m.MessageId)
            .ToListAsync(ct);
        var existing = existingMessageIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var nextSequence = await memoryDb.Messages
            .Where(m => m.SessionId == request.SessionId)
            .Select(m => (long?)m.Sequence)
            .MaxAsync(ct) ?? 0;

        var importedCount = 0;
        foreach (var row in transcriptRows)
        {
            var messageId = BuildTranscriptMessageId(row.Id, request.SessionId);
            if (existing.Contains(messageId))
                continue;

            memoryDb.Messages.Add(new MessageEntity
            {
                MessageId = messageId,
                SessionId = request.SessionId,
                Sequence = ++nextSequence,
                Role = string.IsNullOrWhiteSpace(row.Role) ? "user" : row.Role,
                ContentType = "text",
                Content = row.Content,
                ThinkingJson = row.ThinkingJson,
                UsageJson = row.UsageJson,
                AgentId = string.IsNullOrWhiteSpace(row.AgentInstanceId) ? agentId : row.AgentInstanceId,
                Source = "chat_transcript",
                CreatedAt = row.CreatedAt,
            });
            importedCount++;
        }

        if (session is not null)
        {
            session.MessageCount += importedCount;
        }

        try
        {
            await memoryDb.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
        {
            // UNIQUE 冲突：上次 compact 可能已部分导入相同消息（跨 session 的 chat- 前缀冲突或旧导入残留）。
            // 逐条重试，跳过冲突消息。
            _logger.LogWarning(
                ex,
                "[ContextCompaction] UNIQUE conflict during transcript import session={SessionId}; retrying with per-message skip",
                request.SessionId);

            memoryDb.ChangeTracker.Clear();
            // 重新 attach/lookup Session 以避免 Clear 丢失
            var retrySession = await memoryDb.Sessions
                .FirstOrDefaultAsync(s => s.SessionId == request.SessionId, ct);
            if (retrySession is null)
            {
                session = new SessionEntity
                {
                    SessionId = request.SessionId,
                    WorkspaceId = request.WorkspaceId,
                    AgentId = agentId ?? string.Empty,
                    Status = "active",
                    CreatedAt = firstRow.CreatedAt,
                    LastActivityAt = lastActivityAt,
                };
                memoryDb.Sessions.Add(session);
            }
            else
            {
                session = retrySession;
            }

            var retryImported = 0;
            foreach (var row in transcriptRows)
            {
                var messageId = BuildTranscriptMessageId(row.Id, request.SessionId);
                if (existing.Contains(messageId))
                    continue;

                try
                {
                    memoryDb.Messages.Add(new MessageEntity
                    {
                        MessageId = messageId,
                        SessionId = request.SessionId,
                        Sequence = ++nextSequence,
                        Role = string.IsNullOrWhiteSpace(row.Role) ? "user" : row.Role,
                        ContentType = "text",
                        Content = row.Content,
                        ThinkingJson = row.ThinkingJson,
                        UsageJson = row.UsageJson,
                        AgentId = string.IsNullOrWhiteSpace(row.AgentInstanceId) ? agentId : row.AgentInstanceId,
                        Source = "chat_transcript",
                        CreatedAt = row.CreatedAt,
                    });
                    await memoryDb.SaveChangesAsync(ct);
                    retryImported++;
                }
                catch (DbUpdateException retryEx) when (retryEx.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
                {
                    memoryDb.ChangeTracker.Clear();
                }
            }
            importedCount = retryImported;
        }

        _logger.LogInformation(
            "[ContextCompaction] Imported current session transcript session={SessionId} rows={RowCount}",
            request.SessionId,
            transcriptRows.Count);
    }

    private static Task<List<MessageEntity>> LoadActiveMessagesAsync(
        MemoryDbContext db,
        string sessionId,
        CancellationToken ct) =>
        db.Messages
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .OrderBy(m => m.Sequence)
            .Take(MaxActiveMessagesToLoad)
            .ToListAsync(ct);

    /// <summary>
    /// 用 SQL 聚合估算活跃消息的 token 数，避免对大 session 做全量加载导致 OOM/超时。
    /// 先取 COUNT 和内容总长度做快速估算；如果消息数超过采样阈值则按采样比例外推。
    /// </summary>
        private async Task<int> EstimateActiveTokensAsync(
        MemoryDbContext db,
        string sessionId,
        CancellationToken ct)
    {
        var messageCount = await db.Messages
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .CountAsync(ct);

        if (messageCount == 0)
            return 0;

        var sampleSize = Math.Min(messageCount, MaxHealthEstimateSampleSize);
        var sampleContents = await db.Messages
            .Where(m => m.SessionId == sessionId && m.CompactedBy == null)
            .OrderBy(m => m.Sequence)
            .Select(m => m.Content)
            .Take(sampleSize)
            .ToListAsync(ct);

        var sampleTokens = sampleContents.Sum(content => ContextUsageSnapshotStore.CountTokens(content));
        var estimatedTokens = messageCount <= MaxHealthEstimateSampleSize
            ? sampleTokens
            : sampleTokens / (double)Math.Max(1, sampleSize) * messageCount;

        var result = Math.Max(1, (int)Math.Ceiling(estimatedTokens));

        if (messageCount > MaxHealthEstimateSampleSize)
        {
            _logger.LogWarning(
                "[ContextCompaction] Health estimate sampled with tokenizer session={SessionId} total={TotalMessages} sample={SampleSize} estTokens={EstTokens}",
                sessionId, messageCount, sampleSize, result);
        }

        return result;
    }

    private static int EstimateMessages(IReadOnlyList<MessageEntity> messages) =>
        messages.Sum(m => ContextUsageSnapshotStore.CountTokens(m.Content));




    /// <summary>
    /// 将压缩前冲洗提取的关键事实追加到摘要末尾，保证项目路径、用户偏好等
    /// 重要信息随压缩摘要长期存续：摘要消息始终位于压缩后的上下文中，
    /// 并经 SessionSummaryStore 进入跨会话召回，实现“保证注入”。
    /// </summary>
    private static string AppendKeyFactsSection(string summary, IReadOnlyList<string>? preCompactionFacts)
    {
        if (preCompactionFacts is null || preCompactionFacts.Count == 0)
            return summary;

        var lines = preCompactionFacts
            .Take(24)
            .Select(f => (f ?? string.Empty).Trim())
            .Where(f => f.Length > 0)
            .Select(f => "- " + f)
            .ToArray();
        if (lines.Length == 0)
            return summary;

        var nl = Environment.NewLine;
        return summary.TrimEnd()
            + nl + nl
            + "## 关键事实（自动提取，须长期保留）" + nl
            + string.Join(nl, lines);
    }

    /// <summary>
    /// 合并压缩前冲洗（Flash LLM 从原文提取）的事实与摘要派生 notes。
    /// 原文事实优先（信息保真度更高），去重并限制总量，保证下游整合提示词有界。
    /// </summary>
    private static IReadOnlyList<string> MergeMemoryNotes(
        IReadOnlyList<string>? preCompactionFacts,
        IReadOnlyList<string> summaryNotes)
    {
        if (preCompactionFacts is null || preCompactionFacts.Count == 0)
            return summaryNotes;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>(preCompactionFacts.Count + summaryNotes.Count);
        foreach (var note in preCompactionFacts.Concat(summaryNotes))
        {
            var trimmed = (note ?? string.Empty).Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
                continue;
            merged.Add(trimmed);
        }

        return merged.Count <= 24 ? merged : [.. merged.Take(24)];
    }

    private static IReadOnlyList<string> ExtractMemoryNotes(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
            return [];

        var lines = summary.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var notes = new List<string>();
        var inSection = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                inSection = line.Equals("## Memory Notes", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("## 记忆线索", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("## 需保存记忆", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inSection)
                continue;

            if (!line.StartsWith("- ", StringComparison.Ordinal)
                && !line.StartsWith("* ", StringComparison.Ordinal))
            {
                continue;
            }

            var note = line[2..].Trim();
            if (string.IsNullOrWhiteSpace(note)
                || note.Equals("无", StringComparison.OrdinalIgnoreCase)
                || note.Equals("none", StringComparison.OrdinalIgnoreCase)
                || note.Equals("n/a", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            notes.Add(note);
        }

        return notes;
    }

    private static string BuildPreview(string text)
    {
        var normalized = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160];
    }

    private static string TruncateContent(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
            return text;
        return text[..maxLength] + "…";
    }

    private static string BuildTranscriptMessageId(long transcriptId, string sessionId) =>
        $"chat-{sessionId[..Math.Min(8, sessionId.Length)]}-{transcriptId}";

    private static SummaryInputWindow SelectSummaryInputWindow(MinimumInputExpansion expandedInput)
    {
        var messages = expandedInput.Messages;
        if (messages.Count <= MaxCompactionInputMessages)
        {
            return new SummaryInputWindow(
                messages,
                expandedInput.SupplementalBeforeCount,
                expandedInput.FirstSupplementalSequence,
                expandedInput.LastSupplementalSequence,
                OmittedBeforeCount: 0,
                FirstOmittedSequence: null,
                LastOmittedSequence: null);
        }

        var skipped = messages.Count - MaxCompactionInputMessages;
        var selected = messages
            .Skip(skipped)
            .ToList();

        return new SummaryInputWindow(
            selected,
            expandedInput.SupplementalBeforeCount,
            expandedInput.FirstSupplementalSequence,
            expandedInput.LastSupplementalSequence,
            skipped,
            messages[0].Sequence,
            messages[skipped - 1].Sequence);
    }

    private async Task<MinimumInputExpansion> ExpandToMinimumInputAsync(
        MemoryDbContext db,
        string sessionId,
        IReadOnlyList<MessageEntity> baseMessages,
        CancellationToken ct)
    {
        if (baseMessages.Count == 0 || baseMessages.Count >= MinCompactionInputMessages)
        {
            return new MinimumInputExpansion(
                baseMessages,
                SupplementalBeforeCount: 0,
                FirstSupplementalSequence: null,
                LastSupplementalSequence: null);
        }

        var firstSequence = baseMessages.Min(m => m.Sequence);
        var needed = MinCompactionInputMessages - baseMessages.Count;
        var supplemental = await db.Messages
            .AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .Where(m => m.Sequence < firstSequence)
            .Where(m => m.ContentType == "text")
            .Where(m => m.Role != "system")
            .OrderByDescending(m => m.Sequence)
            .Take(needed)
            .ToListAsync(ct);
        supplemental.Reverse();

        if (supplemental.Count == 0)
        {
            return new MinimumInputExpansion(
                baseMessages,
                SupplementalBeforeCount: 0,
                FirstSupplementalSequence: null,
                LastSupplementalSequence: null);
        }

        return new MinimumInputExpansion(
            supplemental.Concat(baseMessages).ToList(),
            supplemental.Count,
            supplemental[0].Sequence,
            supplemental[^1].Sequence);
    }

    private static string ApplySummaryInputNotice(string summary, SummaryInputWindow input)
    {
        var notices = new List<string>();
        if (input.SupplementalBeforeCount > 0
            && input.FirstSupplementalSequence is not null
            && input.LastSupplementalSequence is not null)
        {
            notices.Add(
                $"当前可压缩窗口不足 MIN={MinCompactionInputMessages}，本次已从同一 session 更早窗口补读 Sequence {input.FirstSupplementalSequence}-{input.LastSupplementalSequence}（{input.SupplementalBeforeCount} 条）。");
        }

        if (input.OmittedBeforeCount > 0
            && input.FirstIncludedSequence is not null
            && input.LastIncludedSequence is not null
            && input.FirstOmittedSequence is not null
            && input.LastOmittedSequence is not null)
        {
            notices.Add(
                $"本次会话压缩只收录当前 session 窗口内消息 Sequence {input.FirstIncludedSequence}-{input.LastIncludedSequence}（{input.Messages.Count} 条）。更早的 Sequence {input.FirstOmittedSequence}-{input.LastOmittedSequence}（{input.OmittedBeforeCount} 条）已移出 active 上下文，但未纳入本次摘要证据。");
        }

        if (notices.Count == 0)
        {
            return summary;
        }

        var notice = $"""
> 压缩范围提示：{string.Join(" ", notices)}

""";
        return notice + summary.TrimStart();
    }

    private sealed record MinimumInputExpansion(
        IReadOnlyList<MessageEntity> Messages,
        int SupplementalBeforeCount,
        long? FirstSupplementalSequence,
        long? LastSupplementalSequence);

    private sealed record SummaryInputWindow(
        IReadOnlyList<MessageEntity> Messages,
        int SupplementalBeforeCount,
        long? FirstSupplementalSequence,
        long? LastSupplementalSequence,
        int OmittedBeforeCount,
        long? FirstOmittedSequence,
        long? LastOmittedSequence)
    {
        public long? FirstIncludedSequence => Messages.Count == 0 ? null : Messages[0].Sequence;

        public long? LastIncludedSequence => Messages.Count == 0 ? null : Messages[^1].Sequence;
    }
}

/// <summary>
/// 抽取式兜底摘要生成器：噪声过滤 + 结构化事实抽取 + 去重的可用摘要。
/// 不再 TakeLast(8) 丢弃内容，而是保留最近 3 条非噪声消息（时效），
/// 再按信息密度评分补选最多 9 条（按原顺序输出），
/// 并从消息中正则抽取 Windows 绝对路径、file:line 标记与 commit 短哈希，
/// 用于填充摘要各章节；偏好章节与 Memory Notes 使用不同批次内容，避免重复。
/// </summary>
public sealed class ExtractiveContextCompactionSummaryGenerator : IContextCompactionSummaryGenerator
{
    private const int MaxPathFacts = 15;
    private const int MaxPreferenceExcerpts = 6;
    private const int MaxRecencyMessages = 3;
    private const int MaxDenseMessages = 9;
    private const int MaxExcerptChars = 400;
    private const int MaxContentScore = 2000;
    private const int FactScorePerMatch = 300;
    private const int AgentRolePriorityBonus = 200;

    private static readonly Regex WindowsPathRegex = new(
        @"[A-Za-z]:\\[\w .-]+(?:\\[\w .-]+)*",
        RegexOptions.Compiled);

    private static readonly Regex FileLineRegex = new(
        @"\b[\w.-]+\.(?:cs|sql|json|md|ts|tsx|ps1):\d+\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommitHashRegex = new(
        @"\b[0-9a-fA-F]{7,12}\b",
        RegexOptions.Compiled);

    private static readonly string[] ErrorKeywords =
    [
        "error", "exception", "failed", "失败", "异常", "错误",
    ];

    private static readonly string[] ToolKeywords =
    [
        "tool", "命令", "执行", "运行", "result", "输出",
    ];

    public Task<string> GenerateSummaryAsync(
        ContextCompactionSummaryRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var allMessages = request.Messages
            .Where(m => !string.IsNullOrWhiteSpace(m.Content))
            .OrderBy(m => m.Sequence)
            .ToList();
        var nonNoise = allMessages
            .Where(m => !IsNoise(m.Content))
            .ToList();

        var userCount = nonNoise.Count(m => IsUserRole(m.Role));
        var agentCount = nonNoise.Count(m => IsAgentRole(m.Role));

        // 结构化事实抽取（跨所有非噪声消息）
        var facts = ExtractFacts(nonNoise);

        // 消息选择：最后 3 条非噪声（时效）+ 按信息密度评分最多 9 条，按原顺序输出
        var recency = nonNoise
            .TakeLast(Math.Min(MaxRecencyMessages, nonNoise.Count))
            .ToList();
        var scoringPool = nonNoise.Count <= recency.Count
            ? []
            : nonNoise
                .Take(nonNoise.Count - recency.Count)
                .Select(m => (Message: m, Score: ScoreMessage(m)))
                .OrderByDescending(x => x.Score)
                .Take(MaxDenseMessages)
                .Select(x => x.Message)
                .ToList();
        var selected = recency
            .Concat(scoringPool)
            .OrderBy(m => m.Sequence)
            .ToList();

        var nl = Environment.NewLine;

        // ── 涉及文件和代码位置：路径 + file:line（没有才写“未检测到”）──
        var locationItems = facts.Paths
            .Concat(facts.FileLines)
            .Select(p => $"- {p}")
            .ToList();
        var locationSection = locationItems.Count > 0
            ? string.Join(nl, locationItems)
            : "- 未检测到涉及的文件或代码位置。";

        // ── 已完成事项：检测到的 commit 列表 + 压缩规模 ──
        var completedItems = facts.Commits
            .Select(c => $"- 提交 {c}")
            .ToList();
        completedItems.Add(
            $"- 已压缩 {request.Messages.Count} 条早期消息，其中用户消息 {userCount} 条，Agent/助手消息 {agentCount} 条。");
        var completedSection = string.Join(nl, completedItems);

        // ── 保留的用户偏好和约束：只放用户消息摘录（最多 6 条）──
        var preferenceItems = nonNoise
            .Where(m => IsUserRole(m.Role))
            .TakeLast(MaxPreferenceExcerpts)
            .Select(m => $"- [user #{m.Sequence}] {TruncateBySentence(m.Content, MaxExcerptChars)}")
            .ToList();
        var preferenceSection = preferenceItems.Count > 0
            ? string.Join(nl, preferenceItems)
            : "- 未从压缩内容中检测到明确的用户偏好或约束。";

        // ── Memory Notes：结构化事实 + 偏好要点（与偏好章节不同批次，带前缀、不同截断）──
        var memoryNotes = new List<string>();
        memoryNotes.AddRange(facts.Paths.Select(p => $"- 涉及文件: {p}"));
        memoryNotes.AddRange(facts.FileLines.Select(f => $"- 代码位置: {f}"));
        memoryNotes.AddRange(facts.Commits.Select(c => $"- 提交: {c}"));
        memoryNotes.AddRange(nonNoise
            .Where(m => IsUserRole(m.Role))
            .Take(MaxPreferenceExcerpts)
            .Select(m => $"- 用户偏好: {TruncateBySentence(m.Content, 120)}"));
        var memoryNotesSection = memoryNotes.Count > 0
            ? string.Join(nl, memoryNotes)
            : "- 未检测到需保留的结构化事实。";

        // ── 用户目标：最早的用户消息摘录 ──
        var firstUser = nonNoise.FirstOrDefault(m => IsUserRole(m.Role));
        var goalSection = firstUser is null
            ? "- 根据会话早期内容继续协助用户完成当前任务。"
            : $"- {TruncateBySentence(firstUser.Content, 200)}";

        // ── 关键决策：密度评分靠前的 Agent 消息 ──
        var decisionItems = selected
            .Where(m => IsAgentRole(m.Role))
            .Take(3)
            .Select(m => $"- 基于消息 #{m.Sequence}：{TruncateBySentence(m.Content, 200)}")
            .ToList();
        var decisionSection = decisionItems.Count > 0
            ? string.Join(nl, decisionItems)
            : "- 未检测到明确的决策记录。";

        // ── 工具调用与重要输出 ──
        var toolItems = nonNoise
            .Where(m => IsAgentRole(m.Role)
                && ToolKeywords.Any(k => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(3)
            .Select(m => $"- [agent #{m.Sequence}] {TruncateBySentence(m.Content, 200)}")
            .ToList();
        var toolSection = toolItems.Count > 0
            ? string.Join(nl, toolItems)
            : "- 未检测到结构化工具输出。";

        // ── 错误、阻塞与修复 ──
        var errorItems = nonNoise
            .Where(m => ErrorKeywords.Any(k => m.Content.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(3)
            .Select(m => $"- [{m.Role} #{m.Sequence}] {TruncateBySentence(m.Content, 200)}")
            .ToList();
        var errorSection = errorItems.Count > 0
            ? string.Join(nl, errorItems)
            : "- 未检测到结构化错误。";

        // ── 当前工作状态：最后一条非噪声消息 ──
        var lastMessage = nonNoise.LastOrDefault();
        var statusSection = lastMessage is null
            ? "- 后续上下文应结合最近未压缩消息继续执行。"
            : $"- 最后一条消息（[{lastMessage.Role}] #{lastMessage.Sequence}）：{TruncateBySentence(lastMessage.Content, 200)}";

        // ── 明确的下一步：最近一条用户请求 ──
        var lastUser = nonNoise.LastOrDefault(m => IsUserRole(m.Role));
        var nextSection = lastUser is null
            ? "- 继续根据用户最新请求推进。"
            : $"- 用户最近请求：{TruncateBySentence(lastUser.Content, 200)}";

        var summary = $"""
<compact_summary>
## 用户目标
{goalSection}

## 已完成事项
{completedSection}

## 关键决策
{decisionSection}

## 涉及文件和代码位置
{locationSection}

## 工具调用与重要输出
{toolSection}

## 错误、阻塞与修复
{errorSection}

## 当前工作状态
{statusSection}

## 明确的下一步
{nextSection}

## 保留的用户偏好和约束
{preferenceSection}

## Memory Notes
{memoryNotesSection}
</compact_summary>
""";

        return Task.FromResult(summary);
    }

    /// <summary>
    /// 噪声过滤：跳过 duplicate 占位、/yolo 命令与 Runtime mode 切换的系统消息。
    /// </summary>
    private static bool IsNoise(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return true;

        var trimmed = content.Trim();
        if (trimmed.Equals("/yolo", StringComparison.OrdinalIgnoreCase))
            return true;
        if (content.Contains(AgentExecutionConstants.DuplicateMessagePlaceholder, StringComparison.OrdinalIgnoreCase))
            return true;
        if (content.Contains(AgentExecutionConstants.DuplicateMessagePlaceholderLegacyHyphen, StringComparison.OrdinalIgnoreCase))
            return true;
        if (content.TrimStart().StartsWith("Runtime mode is now Yolo", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsUserRole(string role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase);

    private static bool IsAgentRole(string role) =>
        string.Equals(role, "agent", StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 结构化事实抽取：Windows 绝对路径（路径类去重最多 15 条）、file:line 标记、commit 短哈希。
    /// </summary>
    private static ExtractedFacts ExtractFacts(IEnumerable<ContextCompactionMessage> messages)
    {
        var paths = new List<string>();
        var fileLines = new List<string>();
        var commits = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFileLines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenCommits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var message in messages)
        {
            var content = message.Content ?? string.Empty;

            foreach (Match match in WindowsPathRegex.Matches(content))
            {
                var path = TrimTrailingPunctuation(match.Value);
                if (path.Length >= 3 && seenPaths.Add(path))
                    paths.Add(path);
            }

            foreach (Match match in FileLineRegex.Matches(content))
            {
                if (seenFileLines.Add(match.Value))
                    fileLines.Add(match.Value);
            }

            foreach (Match match in CommitHashRegex.Matches(content))
            {
                if (!HasCommitContext(content, match))
                    continue;

                var hash = match.Value.ToLowerInvariant();
                if (seenCommits.Add(hash))
                    commits.Add(hash);
            }
        }

        return new ExtractedFacts(
            paths.Take(MaxPathFacts).ToList(),
            fileLines,
            commits);
    }

    /// <summary>判断 7-12 位十六进制串附近是否出现 commit/提交/push 等提交语境。</summary>
    private static bool HasCommitContext(string content, Match match)
    {
        var start = Math.Max(0, match.Index - 40);
        var end = Math.Min(content.Length, match.Index + match.Length + 40);
        var context = content[start..end];
        return context.Contains("commit", StringComparison.OrdinalIgnoreCase)
            || context.Contains("提交", StringComparison.Ordinal)
            || context.Contains("push", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>信息密度评分：内容长度（上限 2000）+ 每个结构化事实 300 分 + Agent/assistant 角色优先加分。</summary>
    private static int ScoreMessage(ContextCompactionMessage message)
    {
        var content = message.Content ?? string.Empty;
        var score = Math.Min(content.Length, MaxContentScore);
        score += CountStructuredFacts(content) * FactScorePerMatch;
        if (IsAgentRole(message.Role))
            score += AgentRolePriorityBonus;
        return score;
    }

    private static int CountStructuredFacts(string content)
    {
        var count = WindowsPathRegex.Matches(content).Count;
        count += FileLineRegex.Matches(content).Count;
        foreach (Match match in CommitHashRegex.Matches(content))
        {
            if (HasCommitContext(content, match))
                count++;
        }

        return count;
    }

    /// <summary>
    /// 句子/换行边界截断：在极限内找最后一个 。！？!?\n，找不到时退回最后一个空白，
    /// 每条最多 maxChars 字符，只追加一次 …，禁止词中间硬切。
    /// </summary>
    private static string TruncateBySentence(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var text = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (text.Length <= maxChars)
            return text;

        var window = text[..maxChars];
        var boundary = -1;
        for (var i = window.Length - 1; i >= 0; i--)
        {
            var c = window[i];
            if (c is '。' or '！' or '？' or '!' or '?' or '\n')
            {
                boundary = i;
                break;
            }
        }

        if (boundary < 0)
        {
            for (var i = window.Length - 1; i >= 0; i--)
            {
                if (char.IsWhiteSpace(window[i]))
                {
                    boundary = i;
                    break;
                }
            }
        }

        var cut = boundary > 0 ? boundary + 1 : maxChars;
        return window[..cut] + "…";
    }

    private static string TrimTrailingPunctuation(string value) =>
        value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '，', '。', '；', '、');

    private sealed record ExtractedFacts(
        IReadOnlyList<string> Paths,
        IReadOnlyList<string> FileLines,
        IReadOnlyList<string> Commits);
}
