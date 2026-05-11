using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Platform;
using PuddingMemoryEngine.Data;
using PuddingMemoryEngine.Entities;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private readonly IMemoryLibraryConvenience? _libraryConvenience;
    private readonly IStreamingEventBus? _eventBus;

    public SubconsciousOrchestrator(
        IMemoryLibrary memoryLibrary,
        IMemoryEngine memoryEngine,
        IMemoryLlmClient memoryLlmClient,
        ILogger<SubconsciousOrchestrator> logger,
        IDbContextFactory<MemoryDbContext> memoryDbContextFactory,
        IMemoryLibraryConvenience? libraryConvenience = null,
        IEmbeddingService? embeddingService = null,
        IStreamingEventBus? eventBus = null)
    {
        _memoryLibrary = memoryLibrary;
        _memoryEngine = memoryEngine;
        _memoryLlmClient = memoryLlmClient;
        _embeddingService = embeddingService;
        _logger = logger;
        _memoryDbContextFactory = memoryDbContextFactory;
        _libraryConvenience = libraryConvenience;
        _eventBus = eventBus;
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
            LlmModelId = memoryLlmConfig?.ModelId ?? Environment.GetEnvironmentVariable("MEMORY_LLM_MODEL_ID")
                ?? Environment.GetEnvironmentVariable("LLM_MODEL"),
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

            // 同步写入 IMemoryLibrary，使召回路径（ContextPipeline L4/L6）可检索到这些事实
            if (_libraryConvenience is not null)
            {
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
                            var writeResult = await _libraryConvenience.UpsertExperienceAsync(
                                job.WorkspaceId,
                                experience with { SourceSessionId = job.SessionId },
                                ct);

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
            }

            log.Status = "completed";
            log.FactsMerged = factsMerged;
            log.FactsDiscarded = factsDiscarded;
            log.LlmTokensUsed = 0;
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
        var memoryLlmConfig = ResolveMemoryLlmConfig();
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

        var raw = await _memoryLlmClient.ChatWithConfigAsync(
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

        var json = ExtractJson(raw);
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
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
            return trimmed;

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];

        return null;
    }

    private async Task<string?> ChatMemoryLlmWithTimeoutAsync(
        string systemPrompt,
        string userPrompt,
        MemoryLlmConfig memoryLlmConfig,
        string stage,
        int? round,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            return await _memoryLlmClient.ChatWithConfigAsync(
                systemPrompt,
                userPrompt,
                memoryLlmConfig,
                tools: null,
                ct: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "[Subconscious][RecallAugmented] LLM timeout stage={Stage} round={Round}",
                stage,
                round?.ToString() ?? "-");
            return null;
        }
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

    private static MemoryLlmConfig ResolveMemoryLlmConfig()
    {
        try
        {
            var loaderType = Type.GetType("PuddingRuntime.Services.PuddingConfigLoader, PuddingRuntime", throwOnError: false);
            var resolveMethod = loaderType?.GetMethod("ResolveMemory", BindingFlags.Public | BindingFlags.Static);
            var resolved = resolveMethod?.Invoke(null, null);

            if (resolved is not null)
            {
                var tupleType = resolved.GetType();
                var endpoint = tupleType.GetField("Item1")?.GetValue(resolved) as string;
                var apiKey = tupleType.GetField("Item2")?.GetValue(resolved) as string;
                var model = tupleType.GetField("Item3")?.GetValue(resolved) as string;
                return new MemoryLlmConfig(endpoint, apiKey, model);
            }
        }
        catch
        {
            // 忽略反射失败，走环境变量回退。
        }

        var endpointFallback = Environment.GetEnvironmentVariable("MEMORY_LLM_ENDPOINT")
                               ?? Environment.GetEnvironmentVariable("LLM_ENDPOINT")
                               ?? "https://api.openai.com/v1";
        var apiKeyFallback = Environment.GetEnvironmentVariable("MEMORY_LLM_API_KEY")
                             ?? Environment.GetEnvironmentVariable("LLM_API_KEY")
                             ?? string.Empty;
        var modelFallback = Environment.GetEnvironmentVariable("MEMORY_LLM_MODEL_ID")
                            ?? Environment.GetEnvironmentVariable("LLM_MODEL")
                            ?? "gpt-4o-mini";

        return new MemoryLlmConfig(endpointFallback, apiKeyFallback, modelFallback);
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
}
