using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PuddingMemoryEngine.Services;

/// <summary>
/// 潜意识编排器（阶段 1 骨架实现）。
/// 当前仅提供基础入口与日志，LLM 抽取/合并将在阶段 2 完整实现。
/// </summary>
public sealed class SubconsciousOrchestrator : ISubconsciousOrchestrator
{
    private static readonly AsyncLocal<RecallDiagnostics?> RecallDiagnosticsSlot = new();

    private readonly IMemoryLibrary _memoryLibrary;
    private readonly IMemoryEngine _memoryEngine;
    private readonly IMemoryLlmClient _memoryLlmClient;
    private readonly IEmbeddingService? _embeddingService;
    private readonly ILogger<SubconsciousOrchestrator> _logger;
    private readonly IDbContextFactory<MemoryDbContext> _memoryDbContextFactory;
    private readonly IMemoryLibrarian _memoryLibrarian;
    private readonly IStreamingEventBus? _eventBus;
    private readonly ISkillEvolutionTrajectorySource _skillTrajectorySource;
    private readonly IAgentSkillEvolutionStore _skillStore;
    private readonly SkillEvolutionDeduplicationService _skillDeduplication;

    private SubconsciousSkillEvaluator? _skillEvaluator;
    private SubconsciousSkillEvaluator SkillEvaluator => _skillEvaluator ??= new SubconsciousSkillEvaluator(_memoryLlmClient);

    private SubconsciousLlmInvoker? _llmInvoker;
    private SubconsciousLlmInvoker LlmInvoker => _llmInvoker ??= new SubconsciousLlmInvoker(_memoryLlmClient, _logger);

    public SubconsciousOrchestrator(
        IMemoryLibrary memoryLibrary,
        IMemoryEngine memoryEngine,
        IMemoryLlmClient memoryLlmClient,
        IMemoryLibrarian memoryLibrarian,
        ILogger<SubconsciousOrchestrator> logger,
        IDbContextFactory<MemoryDbContext> memoryDbContextFactory,
        ISkillEvolutionTrajectorySource skillTrajectorySource,
        IAgentSkillEvolutionStore skillStore,
        SkillEvolutionDeduplicationService skillDeduplication,
        IEmbeddingService? embeddingService = null,
        IStreamingEventBus? eventBus = null)
    {
        _memoryLibrary = memoryLibrary;
        _memoryEngine = memoryEngine;
        _memoryLlmClient = memoryLlmClient;
        _memoryLibrarian = memoryLibrarian;
        _embeddingService = embeddingService;
        _logger = logger;
        _memoryDbContextFactory = memoryDbContextFactory;
        _eventBus = eventBus;
        _skillTrajectorySource = skillTrajectorySource;
        _skillStore = skillStore;
        _skillDeduplication = skillDeduplication;
    }

    /// <summary>
    /// 当前异步上下文中的召回诊断数据（供 ContextPipeline 记录 L6 调试日志）。
    /// </summary>
    public static RecallDiagnostics? CurrentRecallDiagnostics => RecallDiagnosticsSlot.Value;

    /// <summary>
    /// 清理当前异步上下文中的召回诊断数据。
    /// </summary>
    public static void ClearCurrentRecallDiagnostics() => RecallDiagnosticsSlot.Value = null;

    /// <summary>
    /// 潜意识整合主流程：加载会话消息 → LLM 抽取 → 去重合并 → 写入事实/偏好 → 写 JobLog。
    /// 注意：异常被吞并记录失败日志，不向 Worker 外抛，符合后台任务容错约束。
    /// </summary>
    public async Task ConsolidateAsync(
        ConsolidationJob job,
        string memorySearchMode,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default)
    {
        _ = _memoryLibrary;
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sw = Stopwatch.StartNew();

        _logger.LogDebug(
            "[Subconscious] Consolidate start session={SessionId} workspace={Workspace} mode={Mode} hasMessages={HasMsg} llmModel={LlmModel}",
            job.SessionId, job.WorkspaceId, memorySearchMode,
            !string.IsNullOrWhiteSpace(job.LastUserMessage),
            memoryLlmConfig?.ModelId ?? "default");

        var log = new SubconsciousJobLogEntity
        {
            JobId = Guid.NewGuid().ToString("N"),
            SessionId = job.SessionId,
            Status = "pending",
            StartedAt = startedAt,
            CreatedAt = startedAt,
            LlmModelId = memoryLlmConfig?.ModelId,
        };

        try
        {
            string conversationText;

            // 优先使用 Job 中直接传递的消息文本（避免 SessionId 跨系统映射问题）
            if (!string.IsNullOrWhiteSpace(job.LastUserMessage) || !string.IsNullOrWhiteSpace(job.LastAssistantReply))
            {
                var sb = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(job.LastUserMessage))
                    sb.AppendLine($"User: {job.LastUserMessage}");
                if (!string.IsNullOrWhiteSpace(job.LastAssistantReply))
                    sb.AppendLine($"Assistant: {job.LastAssistantReply}");
                conversationText = sb.ToString();

                _logger.LogInformation(
                    "[Subconscious] Using direct message text (skip DB query) session={SessionId} workspace={WorkspaceId}",
                    job.SessionId, job.WorkspaceId);
            }
            else
            {
                // 回退：从 DB 查询历史消息
                await using var queryDb = await _memoryDbContextFactory.CreateDbContextAsync(ct);

                var messages = await queryDb.Messages
                    .AsNoTracking()
                    .Where(m => m.SessionId == job.SessionId)
                    .OrderBy(m => m.CreatedAt)
                    .Take(200)
                    .Select(m => new MessageSlice(m.MessageId, m.Role, m.Content, m.CreatedAt))
                    .ToListAsync(ct);

                if (messages.Count == 0)
                {
                    log.Status = "completed";
                    log.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    log.ElapsedMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
                    queryDb.SubconsciousJobLogs.Add(log);
                    await queryDb.SaveChangesAsync(ct);

                    _logger.LogInformation(
                        "[Subconscious] Skip consolidate: no messages session={SessionId} workspace={WorkspaceId}",
                        job.SessionId,
                        job.WorkspaceId);
                    return;
                }

                conversationText = BuildConversation(messages);
            }

            var summary = await ExtractSummaryByLlmAsync(job.SessionId, conversationText, memoryLlmConfig, ct);

            log.FactsExtracted = summary.Facts.Count;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var factsMerged = 0;
            var factsDiscarded = 0;
            var factsToInsert = new List<MemoryFactEntity>();

            await using var db = await _memoryDbContextFactory.CreateDbContextAsync(ct);

            foreach (var fact in summary.Facts)
            {
                if (string.IsNullOrWhiteSpace(fact.Statement))
                {
                    factsDiscarded++;
                    continue;
                }

                var normalizedStatement = fact.Statement.Trim();
                var keyword = PickKeyword(normalizedStatement);

                var candidates = await db.MemoryFacts
                    .Where(f => f.WorkspaceId == job.WorkspaceId
                                && f.Status == "active"
                                && (keyword.Length == 0 || EF.Functions.Like(f.Statement, $"%{keyword}%")))
                    .OrderByDescending(f => f.UpdatedAt)
                    .Take(20)
                    .ToListAsync(ct);

                MemoryFactEntity? bestCandidate = null;
                var bestSimilarity = 0d;
                foreach (var candidate in candidates)
                {
                    var similarity = CalculateStatementSimilarity(normalizedStatement, candidate.Statement);
                    if (similarity > bestSimilarity)
                    {
                        bestSimilarity = similarity;
                        bestCandidate = candidate;
                    }
                }

                if (bestCandidate is not null && bestSimilarity >= 0.8)
                {
                    if (fact.Confidence <= bestCandidate.Confidence)
                    {
                        factsDiscarded++;
                        continue;
                    }

                    bestCandidate.Confidence = Math.Max(bestCandidate.Confidence, fact.Confidence);
                    bestCandidate.AccessCount += 1;
                    bestCandidate.UpdatedAt = now;
                    factsMerged++;
                    continue;
                }

                var entity = new MemoryFactEntity
                {
                    FactId = Guid.NewGuid().ToString("N"),
                    WorkspaceId = job.WorkspaceId,
                    Statement = normalizedStatement,
                    Confidence = Math.Clamp(fact.Confidence, 0, 1),
                    Category = "general",
                    SourceSessionId = job.SessionId,
                    SourceMessageId = fact.SourceMessageId,
                    Tags = summary.SuggestedTags.Count > 0 ? string.Join(',', summary.SuggestedTags) : null,
                    Status = "active",
                    AccessCount = 0,
                    CreatedAt = now,
                    UpdatedAt = now,
                };

                if (_embeddingService is not null)
                {
                    try
                    {
                        var vec = await _embeddingService.GenerateEmbeddingAsync(normalizedStatement, ct);
                        if (vec.Length > 0)
                        {
                            entity.Embedding = new byte[vec.Length * sizeof(float)];
                            Buffer.BlockCopy(vec, 0, entity.Embedding, 0, entity.Embedding.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "[Subconscious] Generate embedding failed for fact session={SessionId}",
                            job.SessionId);
                    }
                }
                else
                {
                    _logger.LogInformation("[Subconscious] Embedding 跳过：无可用服务");
                }

                factsToInsert.Add(entity);
            }

            if (factsToInsert.Count > 0)
                await db.MemoryFacts.AddRangeAsync(factsToInsert, ct);

            foreach (var pref in summary.Preferences)
            {
                if (string.IsNullOrWhiteSpace(pref.Category)
                    || string.IsNullOrWhiteSpace(pref.Key)
                    || string.IsNullOrWhiteSpace(pref.Value))
                {
                    continue;
                }

                var category = pref.Category.Trim();
                var key = pref.Key.Trim();
                var existing = await db.MemoryPreferences
                    .FirstOrDefaultAsync(p => p.WorkspaceId == job.WorkspaceId
                                              && p.Category == category
                                              && p.Key == key, ct);

                if (existing is null)
                {
                    db.MemoryPreferences.Add(new MemoryPreferenceEntity
                    {
                        PreferenceId = Guid.NewGuid().ToString("N"),
                        WorkspaceId = job.WorkspaceId,
                        Category = category,
                        Key = key,
                        Value = pref.Value.Trim(),
                        SourceSessionId = job.SessionId,
                        SourceMessageId = pref.SourceMessageId,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                }
                else if (!string.Equals(existing.Value, pref.Value, StringComparison.Ordinal))
                {
                    existing.Value = pref.Value.Trim();
                    existing.SourceSessionId = job.SessionId;
                    existing.SourceMessageId = pref.SourceMessageId;
                    existing.UpdatedAt = now;
                }
            }

            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 })
            {
                _logger.LogDebug(ex, "[Subconscious] UNIQUE constraint ignored (duplicate preference/fact)");
            }

            // ADR-029: 经 IMemoryLibrarian 写入 Library，不再直接调用 Convenience
            try
            {
                var structuredBooks = BuildStructuredBookExperiences(summary);
                if (structuredBooks.Count > 0)
                {
                    _logger.LogDebug(
                        "[Subconscious] Syncing structured books to Library workspace={Workspace} books={BookCount}",
                        job.WorkspaceId,
                        structuredBooks.Count);

                    foreach (var (bookTitle, experience) in structuredBooks)
                    {
                        var ingestionRequest = new MemoryIngestionRequest(
                            job.WorkspaceId, "", experience with { SourceSessionId = job.SessionId },
                            TargetBookTitle: bookTitle);

                        var writeResult = await _memoryLibrarian.IngestExperienceAsync(ingestionRequest, ct);

                        _logger.LogDebug(
                            "[Subconscious] Library sync done workspace={Workspace} book={BookTitle} bookId={BookId}",
                            job.WorkspaceId,
                            bookTitle,
                            writeResult.Book.BookId);
                    }
                }
            }
            catch (Exception libEx)
            {
                _logger.LogWarning(libEx,
                    "[Subconscious] Sync to MemoryLibrary failed session={SessionId}", job.SessionId);
            }

            log.Status = "completed";
            log.FactsMerged = factsMerged;
            log.FactsDiscarded = factsDiscarded;
            log.LlmTokensUsed = summary.LlmUsage?.TotalTokens ?? 0;
            log.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            log.ElapsedMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);

            db.SubconsciousJobLogs.Add(log);
            await db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "[Subconscious] Consolidate completed session={SessionId} workspace={WorkspaceId} mode={Mode} extracted={Extracted} merged={Merged} discarded={Discarded}",
                job.SessionId,
                job.WorkspaceId,
                memorySearchMode,
                log.FactsExtracted,
                log.FactsMerged,
                log.FactsDiscarded);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Subconscious] Consolidate failed session={SessionId} workspace={WorkspaceId}",
                job.SessionId,
                job.WorkspaceId);

            try
            {
                await using var failedDb = await _memoryDbContextFactory.CreateDbContextAsync(CancellationToken.None);
                log.Status = "failed";
                log.ErrorMessage = ex.Message;
                log.CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                log.ElapsedMs = (int)Math.Min(int.MaxValue, sw.ElapsedMilliseconds);
                failedDb.SubconsciousJobLogs.Add(log);
                await failedDb.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception writeEx)
            {
                _logger.LogWarning(writeEx, "[Subconscious] Write failed job log failed session={SessionId}", job.SessionId);
            }
        }
    }

    /// <summary>
    /// 阶段 1 占位：返回空摘要结构。
    /// </summary>
    public Task<SessionSummary> SummarizeSessionAsync(
        string sessionId,
        string workspaceId,
        string agentId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Subconscious] Summarize session={SessionId} workspace={WorkspaceId} agent={AgentId} (阶段2实现)",
            sessionId,
            workspaceId,
            agentId);

        var summary = new SessionSummary
        {
            SessionId = sessionId,
            Title = null,
            OneLineSummary = null,
        };
        return Task.FromResult(summary);
    }

    /// <summary>
    /// 增强召回（deep 模式入口）：将所有 MemoryFacts + Preferences 直接交给潜意识 LLM，
    /// LLM 自主判断哪些与用户消息相关，返回带来源的编译结果。
    /// 不做任何 LIKE、分词、FTS5、Tool Calling——LLM 直接阅读全部事实。
    /// </summary>
    public async Task<string?> RecallAugmentedAsync(
        string userMessage,
        string workspaceId,
        string agentId,
        string? sessionId = null,
        int maxTokens = 2000,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(workspaceId))
            return null;

        var totalSw = Stopwatch.StartNew();

        // ── 1. 直接读取全部 MemoryFacts + Preferences，不做任何过滤 ──
        string allFacts;
        await using (var db = await _memoryDbContextFactory.CreateDbContextAsync(ct))
        {
            var facts = await db.MemoryFacts
                .Where(f => f.Status == "active")
                .OrderByDescending(f => f.Confidence)
                .Take(200)  // 单次检索上限
                .Select(f => f.Statement)
                .ToListAsync(ct);

            var prefs = await db.MemoryPreferences
                .OrderByDescending(p => p.CreatedAt)
                .Take(100)
                .Select(p => $"{p.Category}/{p.Key}: {p.Value}")
                .ToListAsync(ct);

            var combined = new List<string>();
            if (facts.Count > 0) combined.Add("MemoryFacts:\n" + string.Join("\n", facts));
            if (prefs.Count > 0) combined.Add("Preferences:\n" + string.Join("\n", prefs));
            allFacts = combined.Count > 0 ? string.Join("\n\n", combined) : "(no stored memories)";
        }

        if (string.IsNullOrWhiteSpace(allFacts) || allFacts == "(no stored memories)")
        {
            totalSw.Stop();
            RecallDiagnosticsSlot.Value = new RecallDiagnostics(1, 0, 0, totalSw.ElapsedMilliseconds);
            return null;
        }

        _logger.LogDebug("[Subconscious][RecallAugmented] Loaded {Len} chars of facts+prefs", allFacts.Length);

        _ = _eventBus?.EmitAsync(new StreamingEvent
        {
            Type = StreamingEventTypes.SubconsciousLoad,
            Data = new { factsCount = allFacts.Split('\n').Length }
        }, ct);

        // ── 2. LLM 直接阅读所有事实，选择相关的内容编译 ──
        if (memoryLlmConfig is null
            || string.IsNullOrWhiteSpace(memoryLlmConfig.Endpoint)
            || string.IsNullOrWhiteSpace(memoryLlmConfig.ApiKey)
            || string.IsNullOrWhiteSpace(memoryLlmConfig.ModelId))
        {
            throw new InvalidOperationException(
                "Memory LLM config is required for deep recall. Configure memory provider/model in llm.providers.json.");
        }

        var systemPrompt =
            "You are a memory retrieval agent. You will receive ALL stored facts and preferences " +
            "about the user. Your job:\n" +
            "1. Read ALL the information carefully.\n" +
            "2. Select ONLY the facts that are relevant to what the user is asking.\n" +
            "3. Compile selected facts into a concise answer. Include source labels like [来自: 个人信息] or [来自: 偏好].\n" +
            "4. If nothing is relevant, say 'no relevant memories'.\n" +
            "5. Match the user's language.\n\n" +
            "Do NOT make up facts. Only use what is provided below.";

        var userPrompt = $"User asked: {userMessage}\n\nALL STORED FACTS AND PREFERENCES:\n{allFacts}\n\nSelect relevant information and compile an answer.";

        _ = _eventBus?.EmitAsync(new StreamingEvent
        {
            Type = StreamingEventTypes.SubconsciousThink,
            Data = new { status = "正在检索相关记忆..." }
        }, ct);

        var result = await ChatMemoryLlmWithTimeoutAsync(
            systemPrompt, userPrompt, memoryLlmConfig, "recall", 0, ct);

        totalSw.Stop();
        var factsCount = allFacts.Split('\n').Length;
        RecallDiagnosticsSlot.Value = new RecallDiagnostics(1, 1, factsCount, totalSw.ElapsedMilliseconds);

        _ = _eventBus?.EmitAsync(new StreamingEvent
        {
            Type = StreamingEventTypes.SubconsciousDone,
            Data = new { resultLen = result?.Length ?? 0, elapsedMs = totalSw.ElapsedMilliseconds }
        }, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(result) || result.Contains("no relevant memories", StringComparison.OrdinalIgnoreCase))
            return null;

        var maxChars = Math.Max(256, maxTokens * 4);
        if (result.Length > maxChars)
            result = result[..maxChars];

        _logger.LogInformation(
            "[Subconscious][RecallAugmented] complete facts={FactsCount} elapsed={ElapsedMs}ms resultLen={ResultLen}",
            factsCount, totalSw.ElapsedMilliseconds, result.Length);

        return result;
    }
    public Task<MemoryDashboard> GetMemoryDashboardAsync(
        string workspaceId,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Subconscious] Get dashboard workspace={WorkspaceId} (阶段2实现)",
            workspaceId);

        var dashboard = new MemoryDashboard
        {
            TotalBooks = 0,
            TotalChapters = 0,
            TotalFacts = 0,
            TotalPointers = 0,
            LastConsolidationAt = null,
        };
        return Task.FromResult(dashboard);
    }

    /// <summary>
    /// 阶段 1 占位：返回空搜索结果。
    /// </summary>
    public Task<MemorySearchResult> SearchMemoriesAsync(
        MemorySearchRequest request,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "[Subconscious] Search memories workspace={WorkspaceId} query={Query} page={Page} pageSize={PageSize} (阶段2实现)",
            request.WorkspaceId,
            request.Query,
            request.Page,
            request.PageSize);

        var result = new MemorySearchResult
        {
            TotalCount = 0,
            Page = request.Page,
        };
        return Task.FromResult(result);
    }

    private async Task<SessionSummary> ExtractSummaryByLlmAsync(
        string sessionId,
        string conversationMessages,
        MemoryLlmConfig? memoryLlmConfig,
        CancellationToken ct)
    {
        const string systemPrompt =
            "You are a fact extraction engine. Extract ALL factual information from ANY language conversation.\n" +
            "Rules:\n" +
            "- Every name, age, location, preference, like, dislike, hobby, favorite thing IS a fact\n" +
            "- Output ONLY a JSON object with keys: facts, preferences, one_line_summary, suggested_tags\n" +
            "- facts: array of {statement, confidence} objects (statement in the original language)\n" +
            "- preferences: array of {category, key, value} objects\n" +
            "- Never output empty arrays when the conversation contains information\n" +
            "- Handle Chinese, English, and mixed-language conversations equally well";

        var userPrompt =
            "Example 1 (English):\n" +
            "User: My name is Bob, I like coffee\n" +
            "Assistant: Got it!\n" +
            "Output: {\"facts\":[{\"statement\":\"User's name is Bob\",\"confidence\":0.95}],\"preferences\":[{\"category\":\"drink\",\"key\":\"likes\",\"value\":\"coffee\"}],\"one_line_summary\":\"User Bob likes coffee\",\"suggested_tags\":[\"personal_info\",\"preferences\"]}\n\n" +
            "Example 2 (Chinese):\n" +
            "User: 我喜欢的水果是苹果\n" +
            "Assistant: 苹果是个好选择！\n" +
            "Output: {\"facts\":[{\"statement\":\"用户喜欢的水果是苹果\",\"confidence\":0.95}],\"preferences\":[{\"category\":\"food\",\"key\":\"favorite_fruit\",\"value\":\"苹果\"}],\"one_line_summary\":\"用户喜欢苹果\",\"suggested_tags\":[\"preferences\",\"food\"]}\n\n" +
            "Now process this conversation:\n" + conversationMessages;

        var (raw, usage) = await _memoryLlmClient.ChatWithUsageAsync(
            systemPrompt,
            userPrompt,
            memoryLlmConfig,
            tools: null,
            ct: ct);

        _logger.LogInformation(
            "[Subconscious] LLM response received session={SessionId} rawLen={RawLen} rawPreview={RawPreview}",
            sessionId,
            raw?.Length ?? 0,
            raw?.Length > 200 ? raw[..200] + "…" : raw ?? "NULL");

        var json = ExtractJson(raw!);
        if (string.IsNullOrWhiteSpace(json))
        {
            _logger.LogWarning(
                "[Subconscious] Failed to extract JSON from LLM response session={SessionId} rawLen={RawLen}",
                sessionId, raw?.Length ?? 0);
            return new SessionSummary { SessionId = sessionId };
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ExtractionPayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload is null)
                return new SessionSummary { SessionId = sessionId };

            return new SessionSummary
            {
                SessionId = sessionId,
                LlmUsage = usage,
                OneLineSummary = payload.OneLineSummary,
                SuggestedTags = payload.SuggestedTags?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList() ?? [],
                Facts = payload.Facts?.Where(f => !string.IsNullOrWhiteSpace(f.Statement))
                    .Select(f => new ExtractedFact
                    {
                        Statement = f.Statement!.Trim(),
                        Confidence = Math.Clamp(f.Confidence ?? 0.8, 0, 1),
                    })
                    .ToList() ?? [],
                Preferences = payload.Preferences?.Where(p => !string.IsNullOrWhiteSpace(p.Category)
                                                              && !string.IsNullOrWhiteSpace(p.Key)
                                                              && !string.IsNullOrWhiteSpace(p.Value))
                    .Select(p => new ExtractedPreference
                    {
                        Category = p.Category!.Trim(),
                        Key = p.Key!.Trim(),
                        Value = p.Value!.Trim(),
                    })
                    .ToList() ?? [],
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Subconscious] Parse extraction JSON failed rawLen={Len}", raw?.Length ?? 0);
            return new SessionSummary { SessionId = sessionId };
        }
    }

    private static string BuildConversation(IReadOnlyList<MessageSlice> messages)
    {
        const int maxChars = 32_000;
        var sb = new StringBuilder(capacity: Math.Min(maxChars, 16_384));

        foreach (var m in messages)
        {
            var role = string.IsNullOrWhiteSpace(m.Role) ? "unknown" : m.Role.Trim().ToLowerInvariant();
            var content = string.IsNullOrWhiteSpace(m.Content) ? "[empty]" : m.Content.Trim();
            var line = $"[{role}] {content}";

            if (sb.Length + line.Length + 1 > maxChars)
                break;

            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static string PickKeyword(string statement)
    {
        var tokens = statement
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：', '、', '(', ')', '[', ']', '{', '}', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .OrderByDescending(t => t.Length)
            .Take(1)
            .ToArray();

        return tokens.Length == 0 ? string.Empty : tokens[0];
    }

    private static double CalculateStatementSimilarity(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0)
            return 0;

        var intersection = setA.Intersect(setB, StringComparer.OrdinalIgnoreCase).Count();
        var union = setA.Union(setB, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
    {
        var items = text
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：', '、', '(', ')', '[', ']', '{', '}', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2);
        return items.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractJson(string raw)
    {
        return SubconsciousSkillEvaluator.ExtractJson(raw);
    }

    private Task<string?> ChatMemoryLlmWithTimeoutAsync(
        string systemPrompt,
        string userPrompt,
        MemoryLlmConfig memoryLlmConfig,
        string stage,
        int? round,
        CancellationToken ct)
    {
        return LlmInvoker.ChatWithTimeoutAsync(systemPrompt, userPrompt, memoryLlmConfig, stage, round, ct);
    }

    private static string NormalizeSnippet(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            return string.Empty;

        return snippet.Trim().Replace("\r", " ").Replace("\n", " ");
    }

    /// <summary>
    /// 解析 LLM 返回的 tool_calls（OpenAI-compatible 格式）。
    /// 返回 (Query, Book?) 元组列表；如果 LLM 未调用任何工具则返回空。
    /// </summary>
    private static List<(string Query, string? Book)> TryParseToolCalls(string? rawResponse)
    {
        var result = new List<(string Query, string? Book)>();
        if (string.IsNullOrWhiteSpace(rawResponse))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(rawResponse);

            // OpenAI 完整响应: choices[0].message.tool_calls[]
            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("message", out var message)
                || !message.TryGetProperty("tool_calls", out var tcArray))
                return result;

            foreach (var tc in tcArray.EnumerateArray())
            {
                if (!tc.TryGetProperty("function", out var func))
                    continue;
                if (!func.TryGetProperty("name", out var nameEl) || nameEl.GetString() != "search_memory")
                    continue;
                if (!func.TryGetProperty("arguments", out var argsEl))
                    continue;

                var argsJson = argsEl.GetString();
                if (string.IsNullOrWhiteSpace(argsJson))
                    continue;

                using var argsDoc = JsonDocument.Parse(argsJson);
                var query = argsDoc.RootElement.TryGetProperty("query", out var q) ? q.GetString() : null;
                var book = argsDoc.RootElement.TryGetProperty("book", out var b) ? b.GetString() : null;

                if (query is not null)
                    result.Add((query, book));
            }
        }
        catch
        {
            return result;
        }

        return result;
    }

    /// <summary>
    /// 直接查询 MemoryFacts 表作为 Library 搜索的兜底（LIKE 匹配）。
    private static IReadOnlyList<(string BookTitle, ExperiencePackage Experience)> BuildStructuredBookExperiences(SessionSummary summary)
    {
        var books = new List<(string BookTitle, ExperiencePackage Experience)>();

        var personalFacts = summary.Facts
            .Select(f => f.Statement)
            .Where(IsPersonalInfoFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (personalFacts.Count > 0)
        {
            books.Add((
                "用户档案",
                new ExperiencePackage
                {
                    Title = "用户档案",
                    Content = BuildChapterContent("个人信息", personalFacts),
                    SuggestedTags = ["用户档案", "个人信息"],
                    Importance = 0.8,
                }));
        }

        var foodPreferences = new List<string>();
        var generalPreferences = new List<string>();

        foreach (var pref in summary.Preferences)
        {
            var prefText = $"{pref.Category}/{pref.Key}: {pref.Value}";
            if (IsFoodPreference(pref.Category, pref.Key))
                foodPreferences.Add(prefText);
            else
                generalPreferences.Add(prefText);
        }

        foreach (var fact in summary.Facts.Select(f => f.Statement).Where(IsPreferenceFact))
        {
            if (IsFoodPreference(string.Empty, fact))
                foodPreferences.Add(fact);
            else
                generalPreferences.Add(fact);
        }

        var preferenceSections = new List<(string ChapterTitle, IReadOnlyList<string> Lines)>();
        if (foodPreferences.Count > 0)
            preferenceSections.Add(("食物偏好", foodPreferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
        if (generalPreferences.Count > 0)
            preferenceSections.Add(("兴趣爱好与其他偏好", generalPreferences.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

        if (preferenceSections.Count > 0)
        {
            books.Add((
                "用户偏好",
                new ExperiencePackage
                {
                    Title = "用户偏好",
                    Content = string.Join("\n\n", preferenceSections.Select(s => BuildChapterContent(s.ChapterTitle, s.Lines))),
                    SuggestedTags = ["用户偏好", "偏好"],
                    Importance = 0.75,
                }));
        }

        if (!string.IsNullOrWhiteSpace(summary.OneLineSummary))
        {
            books.Add((
                "对话摘要",
                new ExperiencePackage
                {
                    Title = "对话摘要",
                    Content = BuildChapterContent("会话摘要", [summary.OneLineSummary.Trim()]),
                    SuggestedTags = ["对话摘要", "会话"],
                    Importance = 0.7,
                }));
        }

        var planFacts = summary.Facts
            .Select(f => f.Statement)
            .Where(IsPlanFact)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (planFacts.Count > 0)
        {
            books.Add((
                "计划与任务",
                new ExperiencePackage
                {
                    Title = "计划与任务",
                    Content = BuildChapterContent("计划事项", planFacts),
                    SuggestedTags = ["计划与任务", "待办"],
                    Importance = 0.8,
                }));
        }

        return books;
    }

    private static string BuildChapterContent(string chapterTitle, IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Chapter: {chapterTitle}");
        foreach (var line in lines.Where(s => !string.IsNullOrWhiteSpace(s)))
            sb.AppendLine($"- {line.Trim()}");
        return sb.ToString().TrimEnd();
    }

    private static bool IsPersonalInfoFact(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        var normalized = statement.Trim().ToLowerInvariant();
        var keys = new[] { "name", "age", "location", "live", "born", "职业", "名字", "年龄", "住", "来自", "城市", "工作" };
        return keys.Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPreferenceFact(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        var normalized = statement.Trim().ToLowerInvariant();
        var keys = new[] { "like", "prefer", "favorite", "dislike", "hobby", "喜欢", "偏好", "爱好", "最爱", "不喜欢" };
        return keys.Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPlanFact(string statement)
    {
        if (string.IsNullOrWhiteSpace(statement))
            return false;

        var normalized = statement.Trim().ToLowerInvariant();
        var keys = new[] { "plan", "todo", "next", "will", "need to", "打算", "计划", "待办", "将会", "接下来", "准备" };
        return keys.Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFoodPreference(string category, string keyOrStatement)
    {
        var normalized = $"{category} {keyOrStatement}".ToLowerInvariant();
        var foodKeys = new[] { "food", "drink", "fruit", "meal", "coffee", "tea", "吃", "喝", "食物", "水果", "饮食", "拉面", "米饭" };
        return foodKeys.Any(k => normalized.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RoundPlan(IReadOnlyList<string> SearchQueries, bool Enough);

    public sealed record RecallDiagnostics(
        int Rounds,
        int TotalQueries,
        int FoundItemsCount,
        long TotalLatencyMs);

    private sealed record MessageSlice(string MessageId, string Role, string? Content, long CreatedAt);

    private sealed class ExtractionPayload
    {
        [JsonPropertyName("facts")]
        public List<ExtractedFactPayload>? Facts { get; set; }

        [JsonPropertyName("preferences")]
        public List<ExtractedPreferencePayload>? Preferences { get; set; }

        [JsonPropertyName("one_line_summary")]
        public string? OneLineSummary { get; set; }

        [JsonPropertyName("suggested_tags")]
        public List<string>? SuggestedTags { get; set; }
    }

    private sealed class ExtractedFactPayload
    {
        [JsonPropertyName("statement")]
        public string? Statement { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }

    private sealed class ExtractedPreferencePayload
    {
        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("value")]
        public string? Value { get; set; }
    }

    // ============================================================
    // Auto-Dream: 定期记忆整理
    // ============================================================

    public async Task<AutoDreamReport> AutoDreamAsync(
        string workspaceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int suggested = 0, executed = 0, merged = 0, archived = 0, deleted = 0;
        string summary;

        var snapshot = await BuildMemorySnapshotAsync(workspaceId, ct);
        _logger.LogInformation(
            "[AutoDream] Phase1-Scan: {Total} books ({Active} active, {Archived} archived)",
            snapshot.TotalBooks, snapshot.ActiveBooks, snapshot.ArchivedBooks);

        if (snapshot.TotalBooks <= 10 && snapshot.ArchivedBooks == 0)
        {
            summary = "Skipped: too small";
            return new AutoDreamReport { Summary = summary, DurationMs = sw.ElapsedMilliseconds, Timestamp = DateTime.UtcNow };
        }

        var config = memoryLlmConfig ?? new MemoryLlmConfig(null, null, null);
        var plan = await PlanAutoDreamAsync(snapshot, config, ct);

        if (plan is not { Length: > 0 })
        {
            summary = "No operations needed";
            return new AutoDreamReport { Summary = summary, DurationMs = sw.ElapsedMilliseconds, Timestamp = DateTime.UtcNow };
        }

        suggested = plan.Length;

        foreach (var op in plan.Take(5))
        {
            try
            {
                switch (op.Kind)
                {
                    case "merge":
                        if (await ExecuteMergeAsync(op, workspaceId, ct)) { merged++; executed++; }
                        break;
                    case "archive":
                        if (await ExecuteArchiveAsync(op, ct)) { archived++; executed++; }
                        break;
                    case "delete":
                        if (await ExecuteDeleteAsync(op, ct)) { deleted++; executed++; }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AutoDream] Failed: {Kind} {BookId}", op.Kind, op.BookId);
            }
        }

        summary = $"merged {merged}, archived {archived}, deleted {deleted}, {sw.ElapsedMilliseconds}ms";
        _logger.LogInformation("[AutoDream] Completed: {Summary}", summary);
        return new AutoDreamReport
        {
            Merged = merged, Archived = archived, Deleted = deleted,
            Executed = executed, Suggested = suggested,
            DurationMs = sw.ElapsedMilliseconds, Summary = summary,
            Timestamp = DateTime.UtcNow
        };
    }

    private async Task<MemorySnapshot> BuildMemorySnapshotAsync(string workspaceId, CancellationToken ct)
    {
        var books = await _memoryLibrary.ListBooksScopedAsync(workspaceId, limit: 100, ct);
        var list = new List<MemorySnapshotBook>();
        foreach (var b in books)
        {
            var chapters = await _memoryLibrary.ListChaptersAsync(b.BookId, ct);
            list.Add(new MemorySnapshotBook
            {
                BookId = b.BookId, Title = b.Title, Status = b.Status,
                Summary = b.Summary ?? "", ChapterCount = chapters.Count,
                LastUpdated = chapters.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(chapters.Max(c => c.UpdatedAt)).UtcDateTime : null,
                ChapterTitles = chapters.OrderByDescending(c => c.UpdatedAt).Take(10).Select(c => c.Title).ToArray()
            });
        }
        return new MemorySnapshot
        {
            TotalBooks = books.Count, ActiveBooks = books.Count(b => b.Status == "active"),
            ArchivedBooks = books.Count(b => b.Status == "archived"),
            TotalChapters = list.Sum(b => b.ChapterCount), Books = list.ToArray()
        };
    }

    private async Task<AutoDreamOperation[]> PlanAutoDreamAsync(MemorySnapshot snapshot, MemoryLlmConfig config, CancellationToken ct)
    {
        var systemPrompt = @"You are Pudding memory maintenance. Analyze the library snapshot. Rules:
1. Inaccurate/Outdated -> archive
2. Redundant (same Title+Summary) -> merge then archive source
3. archived + >30d no update -> may delete
4. Never delete decision-records, user-profiles, project-knowledge
5. Max 5 operations. Output JSON: {""operations"":[{""kind"":""merge|archive|delete"",""reason"":""..."",""bookId"":""..."",""sourceBookId"":""..."",""priority"":1}]}";

        var userPrompt = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        var raw = await ChatMemoryLlmWithTimeoutAsync(systemPrompt, userPrompt, config, "auto-dream.plan", null, ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var json = ExtractJson(raw);
        if (json == null) return [];
        try
        {
            var plan = JsonSerializer.Deserialize<AutoDreamPlan>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return plan?.Operations ?? [];
        }
        catch { return []; }
    }

    private async Task<bool> ExecuteMergeAsync(AutoDreamOperation op, string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(op.SourceBookId)) return false;
        var source = await _memoryLibrary.GetBookAsync(op.SourceBookId, ct);
        var target = await _memoryLibrary.GetBookAsync(op.BookId, ct);
        if (source == null || target == null) return false;
        var chapters = await _memoryLibrary.ListChaptersAsync(op.SourceBookId, ct);
        foreach (var ch in chapters)
            await _memoryLibrary.AddChapterAsync(op.BookId, ch.Title, ch.Content, sourceSessionId: ch.SourceSessionId, ct: ct);
        await _memoryLibrary.ArchiveBookAsync(op.SourceBookId, ct);
        _logger.LogInformation("[AutoDream] Merged {Src} -> {Tgt} ({N} ch): {R}", source.Title, target.Title, chapters.Count, op.Reason);
        return true;
    }

    private async Task<bool> ExecuteArchiveAsync(AutoDreamOperation op, CancellationToken ct)
    {
        var book = await _memoryLibrary.GetBookAsync(op.BookId, ct);
        if (book == null) return false;
        await _memoryLibrary.ArchiveBookAsync(op.BookId, ct);
        _logger.LogInformation("[AutoDream] Archived {T}: {R}", book.Title, op.Reason);
        return true;
    }

    private async Task<bool> ExecuteDeleteAsync(AutoDreamOperation op, CancellationToken ct)
    {
        var book = await _memoryLibrary.GetBookAsync(op.BookId, ct);
        if (book == null || book.Status != "archived") return false;
        var chapters = await _memoryLibrary.ListChaptersAsync(op.BookId, ct);
        if (chapters.Count > 0)
        {
            var lastMs = chapters.Max(c => c.UpdatedAt);
            var lastDate = DateTimeOffset.FromUnixTimeMilliseconds(lastMs).UtcDateTime;
            if ((DateTime.UtcNow - lastDate).TotalDays < 30) return false;
        }
        await _memoryLibrary.DeleteBookAsync(op.BookId, ct);
        _logger.LogInformation("[AutoDream] Deleted {T}: {R}", book.Title, op.Reason);
        return true;
    }

    private sealed record AutoDreamPlan
    {
        [JsonPropertyName("operations")]
        public AutoDreamOperation[] Operations { get; init; } = [];
    }

    private sealed record AutoDreamOperation
    {
        [JsonPropertyName("kind")] public string Kind { get; init; } = "";
        [JsonPropertyName("reason")] public string Reason { get; init; } = "";
        [JsonPropertyName("bookId")] public string BookId { get; init; } = "";
        [JsonPropertyName("sourceBookId")] public string? SourceBookId { get; init; }
        [JsonPropertyName("priority")] public int Priority { get; init; }
    }

    // ============================================================
    // 管道2：经验→SKILL — Pattern Extraction
    // ============================================================

    public async Task<PatternExtractionReport> ExtractPatternsAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int candidatesFound = 0, promoted = 0, merged = 0, deferred = 0, demotedToMemory = 0, skipped = 0;
        var createdSkillIds = new List<string>();
        var updatedSkillIds = new List<string>();

        _logger.LogInformation(
            "[PatternExtraction] Phase1-Scan: scanning canonical trajectories workspace={Workspace} agent={AgentInstanceId}",
            workspaceId,
            agentInstanceId);
        var candidates = await DetectPatternCandidatesAsync(workspaceId, agentInstanceId, memoryLlmConfig, ct);
        candidatesFound = candidates.Count;
        _logger.LogInformation("[PatternExtraction] Phase1-Scan: found {Count} candidates", candidatesFound);

        if (candidatesFound == 0)
            return new PatternExtractionReport { DurationMs = sw.ElapsedMilliseconds, Summary = "No candidates found", Timestamp = DateTime.UtcNow };

        _logger.LogInformation("[PatternExtraction] Phase2-Filter: evaluating {Count} candidates", candidatesFound);
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var evaluation = await EvaluateCandidateAsync(candidate, memoryLlmConfig, ct);
            switch (evaluation.Decision)
            {
                case "promote":
                    var admission = await _skillDeduplication.EvaluateAdmissionAsync(
                        candidate,
                        memoryLlmConfig,
                        ct);
                    if (string.Equals(admission.Action, SkillAdmissionActions.Create, StringComparison.Ordinal))
                    {
                        var skillId = await MaterializeSkillAsync(candidate, evaluation, ct);
                        if (skillId is not null)
                        {
                            createdSkillIds.Add(skillId);
                            promoted++;
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    else if (string.Equals(admission.Action, SkillAdmissionActions.Merge, StringComparison.Ordinal)
                             && !string.IsNullOrWhiteSpace(admission.TargetSkillId))
                    {
                        var updated = await _skillDeduplication.MergeCandidateAsync(
                            candidate,
                            admission.TargetSkillId,
                            ct);
                        if (updated is not null)
                        {
                            updatedSkillIds.Add(updated.SkillId);
                            merged++;
                        }
                        else
                        {
                            deferred++;
                        }
                    }
                    else if (string.Equals(admission.Action, SkillAdmissionActions.Skip, StringComparison.Ordinal))
                    {
                        skipped++;
                    }
                    else
                    {
                        deferred++;
                    }
                    break;
                case "demote":
                    await SaveAsMemoryNoteAsync(candidate, evaluation, workspaceId, ct);
                    demotedToMemory++;
                    break;
                case "skip":
                    skipped++;
                    break;
            }
        }

        var report = new PatternExtractionReport
        {
            DurationMs = sw.ElapsedMilliseconds, CandidatesFound = candidatesFound,
            Promoted = promoted, Merged = merged, Deferred = deferred,
            DemotedToMemory = demotedToMemory, Skipped = skipped,
            CreatedSkillIds = createdSkillIds.ToArray(),
            UpdatedSkillIds = updatedSkillIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Summary = $"found {candidatesFound}, created {promoted}, merged {merged}, deferred {deferred}, demoted {demotedToMemory}, skipped {skipped}",
            Timestamp = DateTime.UtcNow
        };
        _logger.LogInformation("[PatternExtraction] Completed: {Summary}", report.Summary);
        return report;
    }

    private async Task<List<PatternCandidate>> DetectPatternCandidatesAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig,
        CancellationToken ct)
    {
        var candidates = new List<PatternCandidate>();
        try
        {
            var trajectories = await _skillTrajectorySource.GetRecentSuccessfulAsync(
                workspaceId,
                agentInstanceId,
                limit: 5,
                ct);
            if (trajectories.Count == 0)
            {
                _logger.LogInformation("[PatternExtraction] No verified tool trajectories to scan");
                return candidates;
            }
            var existingSkills = await _skillStore.ListAutoGeneratedAsync(agentInstanceId, ct);
            var processedTurnIds = SkillEvolutionDeduplicationService.ExtractProcessedTurnIds(existingSkills);
            var unprocessedTrajectories = trajectories
                .Where(trajectory => !processedTurnIds.Contains(trajectory.TurnId))
                .ToArray();
            var suppressed = trajectories.Count - unprocessedTrajectories.Length;
            if (suppressed > 0)
            {
                _logger.LogInformation(
                    "[PatternExtraction] Suppressed {Count} already-processed verified trajectories",
                    suppressed);
            }
            foreach (var trajectory in unprocessedTrajectories)
            {
                if (ct.IsCancellationRequested) break;
                var detected = await DetectGoldenPathsInSessionAsync(trajectory, memoryLlmConfig, ct);
                if (detected.Count > 0) candidates.AddRange(detected);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[PatternExtraction] Candidate detection failed"); }
        return candidates;
    }

    private async Task<List<PatternCandidate>> DetectGoldenPathsInSessionAsync(
        SkillEvolutionTrajectory trajectory,
        MemoryLlmConfig? memoryLlmConfig,
        CancellationToken ct)
    {
        const string systemPrompt = "You are a pattern detection engine. The input is a verified successful tool trajectory from the canonical conversation event store. Identify a reusable golden path only when the tool chain solves a generalizable task. Two or more successful tool calls are sufficient. Output JSON: {\"candidates\":[{\"title\":\"short English skill name\",\"goal\":\"what problem this solves\",\"confidence\":0.0-1.0,\"evidence\":\"brief evidence\"}]}. Return {\"candidates\":[]} for one-off or unsafe tasks.";
        var userPrompt = JsonSerializer.Serialize(trajectory);
        var raw = await ChatMemoryLlmWithTimeoutAsync(systemPrompt, userPrompt, memoryLlmConfig ?? new MemoryLlmConfig(null, null, null), "pattern-detect", null, ct);
        if (string.IsNullOrWhiteSpace(raw)) return [];
        var json = ExtractJson(raw);
        if (json is null) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var arr)) return [];
            var list = new List<PatternCandidate>();
            foreach (var el in arr.EnumerateArray())
                list.Add(new PatternCandidate
                {
                    SessionId = trajectory.SessionId,
                    TurnId = trajectory.TurnId,
                    AgentInstanceId = trajectory.AgentInstanceId,
                    Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Goal = el.TryGetProperty("goal", out var g) ? g.GetString() ?? trajectory.Goal : trajectory.Goal,
                    StepsCount = trajectory.Steps.Count,
                    AllSucceeded = true,
                    RetryCount = 0,
                    ToolSequence = trajectory.Steps.Select(step => step.ToolName).ToArray(),
                    Confidence = el.TryGetProperty("confidence", out var cf) ? cf.GetDouble() : 0.5,
                    Evidence = el.TryGetProperty("evidence", out var ev) ? ev.GetString() : null,
                });
            return list;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[PatternExtraction] Parse candidates JSON failed"); return []; }
    }

    private async Task<CandidateEvaluation> EvaluateCandidateAsync(PatternCandidate candidate, MemoryLlmConfig? memoryLlmConfig, CancellationToken ct)
    {
        if (!candidate.AllSucceeded)
        { _logger.LogDebug("[PatternExtraction] Quick-skip {Title}: trajectory is not fully successful", candidate.Title); return new CandidateEvaluation { Decision = "skip", Reason = "Trajectory is not fully successful" }; }
        if (candidate.StepsCount < 2 || candidate.Confidence < 0.65)
        { _logger.LogDebug("[PatternExtraction] Quick-skip {Title}: too few steps ({Steps})", candidate.Title, candidate.StepsCount); return new CandidateEvaluation { Decision = "skip", Reason = $"Too simple ({candidate.StepsCount} steps)" }; }
        const string systemPrompt = "You are a skill quality evaluator. Check: (1) the supplied trajectory is verified successful, (2) the goal and steps are reusable, (3) the generated skill would not encode secrets or destructive one-off state. Output JSON: {\"promoted\":bool,\"decision\":\"promote|demote|skip\",\"reason\":\"...\",\"checks\":[{\"conditionName\":\"passing_check\",\"passed\":bool,\"reason\":\"...\"},{\"conditionName\":\"reusable\",\"passed\":bool,\"reason\":\"...\"},{\"conditionName\":\"safe\",\"passed\":bool,\"reason\":\"...\"}]}. Promote only when all three pass.";
        var raw = await ChatMemoryLlmWithTimeoutAsync(systemPrompt, JsonSerializer.Serialize(candidate), memoryLlmConfig ?? new MemoryLlmConfig(null, null, null), "candidate-eval", null, ct);
        if (string.IsNullOrWhiteSpace(raw)) return new CandidateEvaluation { Decision = "skip", Reason = "LLM timeout" };
        var json = ExtractJson(raw);
        if (json is null) return new CandidateEvaluation { Decision = "skip", Reason = "No JSON" };
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var checks = new List<ConditionCheckResult>();
            if (root.TryGetProperty("checks", out var arr))
                foreach (var chk in arr.EnumerateArray())
                    checks.Add(new ConditionCheckResult { ConditionName = chk.TryGetProperty("conditionName", out var cn) ? cn.GetString() ?? "" : "", Passed = chk.TryGetProperty("passed", out var p) && p.GetBoolean(), Reason = chk.TryGetProperty("reason", out var r) ? r.GetString() : null });
            var requiredChecks = new[] { "passing_check", "reusable", "safe" };
            var allRequiredChecksPassed = requiredChecks.All(required => checks.Any(check =>
                string.Equals(check.ConditionName, required, StringComparison.OrdinalIgnoreCase)
                && check.Passed));
            var promoted = root.TryGetProperty("promoted", out var pr) && pr.GetBoolean()
                           && allRequiredChecksPassed;
            var requestedDecision = root.TryGetProperty("decision", out var d)
                ? d.GetString()
                : null;
            return new CandidateEvaluation
            {
                Promoted = promoted,
                Decision = promoted
                    ? "promote"
                    : string.Equals(requestedDecision, "demote", StringComparison.OrdinalIgnoreCase)
                        ? "demote"
                        : "skip",
                Reason = root.TryGetProperty("reason", out var re) ? re.GetString() : null,
                Checks = checks.ToArray()
            };
        }
        catch { return new CandidateEvaluation { Decision = "skip", Reason = "Parse error" }; }
    }

    private async Task<string?> MaterializeSkillAsync(
        PatternCandidate candidate,
        CandidateEvaluation evaluation,
        CancellationToken ct)
    {
        try
        {
            var skillId = ToSkillId(candidate.Title, candidate.Goal);
            if (await _skillStore.GetAsync(candidate.AgentInstanceId, skillId, ct) is not null)
            {
                _logger.LogInformation(
                    "[PatternExtraction] Skip existing skill {SkillId} agent={AgentInstanceId}",
                    skillId,
                    candidate.AgentInstanceId);
                return null;
            }

            var skillMd = GenerateSkillMarkdown(candidate, evaluation);
            await _skillStore.CreateAsync(candidate.AgentInstanceId, new AgentSkillEvolutionWriteRequest
            {
                SkillId = skillId,
                Name = candidate.Title,
                Version = "1.0.0",
                Description = candidate.Goal,
                Tags =
                [
                    "auto-generated",
                    "self-evolution",
                    $"source-turn:{candidate.TurnId}",
                    $"source-session:{candidate.SessionId}",
                ],
                Keywords = candidate.ToolSequence
                    .Append(candidate.Title)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                Markdown = skillMd,
            }, ct);
            _logger.LogInformation(
                "[PatternExtraction] Created runtime skill {SkillId} agent={AgentInstanceId}",
                skillId,
                candidate.AgentInstanceId);
            return skillId;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[PatternExtraction] Materialize skill failed {Title}", candidate.Title); return null; }
    }

    private static string GenerateSkillMarkdown(PatternCandidate candidate, CandidateEvaluation evaluation)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"name: {ToKebabCase(candidate.Title)}");
        sb.AppendLine("version: 1.0.0");
        sb.AppendLine($"description: {candidate.Goal}");
        sb.AppendLine("tags: [auto-generated, skill-candidate]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {candidate.Title}");
        sb.AppendLine();
        sb.AppendLine("## 来源");
        sb.AppendLine($"- 会话: {candidate.SessionId}");
        sb.AppendLine($"- 置信度: {candidate.Confidence:P0}");
        sb.AppendLine("- 验证状态: canonical conversation events verified all tool calls succeeded");
        sb.AppendLine($"- Turn: {candidate.TurnId}");
        sb.AppendLine();
        sb.AppendLine("## 目标");
        sb.AppendLine(candidate.Goal);
        sb.AppendLine();
        sb.AppendLine("## 步骤");
        for (int i = 0; i < candidate.ToolSequence.Length; i++)
            sb.AppendLine($"{i + 1}. `{candidate.ToolSequence[i]}`");
        sb.AppendLine();
        sb.AppendLine("## 质量门禁");
        foreach (var check in evaluation.Checks)
            sb.AppendLine($"- {check.ConditionName}: {(check.Passed ? "passed" : "failed")} — {check.Reason}");
        if (!string.IsNullOrWhiteSpace(candidate.UserCorrection))
        { sb.AppendLine(); sb.AppendLine("## 用户纠正"); sb.AppendLine(candidate.UserCorrection); }
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"自动生成于 {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("管道2：经验→SKILL | Pudding SubconsciousOrchestrator");
        return sb.ToString();
    }

    private async Task SaveAsMemoryNoteAsync(PatternCandidate candidate, CandidateEvaluation evaluation, string workspaceId, CancellationToken ct)
    {
        try
        {
            var note = $"## 经验笔记: {candidate.Title}\n\n- 目标: {candidate.Goal}\n- 步骤数: {candidate.StepsCount}, 重试: {candidate.RetryCount}\n- 工具序列: {string.Join(" → ", candidate.ToolSequence)}\n- 降级原因: {evaluation.Reason}\n- 来源会话: {candidate.SessionId}\n";
            var ingestion = new MemoryIngestionRequest(workspaceId, "", new ExperiencePackage { Title = $"经验: {candidate.Title}", Content = note, SuggestedTags = ["经验笔记", "待晋级", $"session:{candidate.SessionId}"], Importance = 0.4, SourceSessionId = candidate.SessionId }, TargetBookTitle: "经验教训");
            await _memoryLibrarian.IngestExperienceAsync(ingestion, ct);
            _logger.LogInformation("[PatternExtraction] Saved as memory note {Title}", candidate.Title);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[PatternExtraction] Save memory note failed {Title}", candidate.Title); }
    }

    private static string ToKebabCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "unnamed";
        var result = new StringBuilder();
        var prevLower = false;
        foreach (var ch in input.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (char.IsUpper(ch) && prevLower) result.Append('-');
                result.Append(char.ToLowerInvariant(ch));
                prevLower = char.IsLower(ch);
            }
            else if (ch is ' ' or '-' or '_')
            {
                if (result.Length > 0 && result[^1] != '-') result.Append('-');
                prevLower = false;
            }
        }
        var s = result.ToString().Trim('-');
        return s.Length > 0 ? s : "unnamed";
    }

    private static string ToSkillId(string title, string goal)
    {
        var kebab = ToKebabCase(title);
        var ascii = new string(kebab
            .Where(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            .ToArray())
            .Trim('-');
        if (!string.IsNullOrWhiteSpace(ascii))
            return ascii;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{goal}"));
        return $"skill-{Convert.ToHexStringLower(hash)[..12]}";
    }

    // ── Skill Self-Improvement ──

    public async Task<SkillImprovementReport> ImproveSkillsAsync(
        string workspaceId,
        string agentInstanceId,
        MemoryLlmConfig? memoryLlmConfig = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        int evaluated = 0, patched = 0, skipped = 0;
        var improvedIds = new List<string>();
        var consolidation = new SkillConsolidationResult();

        try
        {
            _logger.LogInformation(
                "[SkillImprovement] Phase1-Scan: listing runtime skills workspace={WorkspaceId} agent={AgentInstanceId}",
                workspaceId,
                agentInstanceId);

            consolidation = await _skillDeduplication.ConsolidateExistingAsync(
                agentInstanceId,
                memoryLlmConfig,
                ct);

            var allSkills = await _skillStore.ListAutoGeneratedAsync(agentInstanceId, ct);
            var candidates = allSkills
                .Where(skill => skill.Enabled)
                .Where(skill => !SkillEvolutionDeduplicationService.HasCurrentEvaluation(skill))
                .Take(5)
                .ToList();

            evaluated = candidates.Count;
            _logger.LogInformation("[SkillImprovement] Phase1-Scan: {Total} total, {Candidates} candidates",
                allSkills.Count, candidates.Count);

            var config = memoryLlmConfig;

            foreach (var skill in candidates)
            {
                ct.ThrowIfCancellationRequested();
                _logger.LogInformation("[SkillImprovement] Phase2-Evaluate: {SkillId}", skill.SkillId);

                var eval = await EvaluateOneSkillAsync(skill, config, ct);
                if (eval is null)
                {
                    skipped++;
                    continue;
                }
                if (!eval.NeedsUpdate)
                {
                    await _skillDeduplication.MarkEvaluatedAsync(agentInstanceId, skill, ct);
                    skipped++;
                    continue;
                }

                _logger.LogInformation("[SkillImprovement] Phase3-Patch: {SkillId} needs update: {Reason}", skill.SkillId, eval.Reason);
                var improved = await GenerateImprovedSkillContentAsync(skill, eval, config, ct);
                if (string.IsNullOrWhiteSpace(improved))
                {
                    skipped++;
                    continue;
                }

                var newVersion = BumpVersion(skill.Version);
                var normalizedImproved = Regex.Replace(
                    improved,
                    @"^(\s*version:\s*)[^\r\n]+$",
                    $"${{1}}{newVersion}",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline);
                await _skillStore.UpdateAsync(agentInstanceId, skill.SkillId, new AgentSkillEvolutionWriteRequest
                {
                    SkillId = skill.SkillId,
                    Name = skill.Name,
                    Version = newVersion,
                    Description = skill.Description,
                    Tags = SkillEvolutionDeduplicationService.WithEvaluationMarker(skill.Tags, newVersion),
                    Keywords = skill.Keywords,
                    Markdown = normalizedImproved,
                }, ct);
                patched++;
                improvedIds.Add(skill.SkillId);
                _logger.LogInformation("[SkillImprovement] Patched {SkillId} {Old}→{New}", skill.SkillId, skill.Version, newVersion);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "[SkillImprovement] Error"); }

        sw.Stop();
        return new SkillImprovementReport
        {
            DurationMs = sw.ElapsedMilliseconds,
            Evaluated = evaluated,
            Patched = patched,
            Consolidated = consolidation.Consolidated,
            Skipped = skipped,
            ImprovedSkillIds = improvedIds.ToArray(),
            DisabledDuplicateSkillIds = consolidation.DisabledSkillIds.ToArray(),
            Summary = $"Consolidated {consolidation.Consolidated} duplicate(s); evaluated {evaluated}, improved {patched}, skipped {skipped}",
            Timestamp = DateTime.UtcNow
        };
    }

    private Task<SkillEvaluation?> EvaluateOneSkillAsync(
        AgentSkillEvolutionDocument skill,
        MemoryLlmConfig? config,
        CancellationToken ct)
    {
        return SkillEvaluator.EvaluateOneSkillAsync(skill, config, ct);
    }

    private async Task<string?> GenerateImprovedSkillContentAsync(
        AgentSkillEvolutionDocument skill,
        SkillEvaluation eval,
        MemoryLlmConfig? config,
        CancellationToken ct)
    {
        var prompt = $@"Improve this Pudding SKILL.

SKILL: {skill.SkillId} v{skill.Version}
NEW VERSION: {BumpVersion(skill.Version)}
REASON FOR UPDATE: {eval.Reason}

CURRENT SKILL.md:
{skill.Markdown}

Output the COMPLETE improved SKILL.md. Preserve original structure. Only fix outdated parts.";

        var raw = await _memoryLlmClient.ChatWithConfigAsync("You improve Pudding SKILL files. Output complete SKILL.md.", prompt, config, tools: null, ct: ct);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static string BumpVersion(string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(currentVersion)) return "1.0.1";
        var parts = currentVersion.TrimStart('v').Split('.');
        if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
        {
            parts[2] = (patch + 1).ToString();
            return string.Join('.', parts);
        }
        return currentVersion + ".1";
    }

    private static string Truncate(string text, int maxChars)
        => string.IsNullOrEmpty(text) || text.Length <= maxChars ? text : text[..maxChars] + "...";

}







