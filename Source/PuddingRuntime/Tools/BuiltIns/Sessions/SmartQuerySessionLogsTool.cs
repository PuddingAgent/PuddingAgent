using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// SmartQuerySessionLogsTool — 语义化会话日志查询。主 Agent 使用自然语言描述"找什么"，
/// 内部委托子代理（deepseek-v4-flash 模型）执行 query_session_logs（grep/messages/list_days/list_sessions），
/// 返回带上下文的摘要结果。一次调用替代原本需要多轮翻页日志的任务。
///
/// 设计原则：
///   · Agent = Function — 工具签名即自然语言，Agent 不需要理解 query_session_logs 的复杂参数
///   · 薄包装 — 核心逻辑在子代理模板中，本工具只做参数映射
///   · MainAgentOnly — 不暴露给子代理，防止 SmartQuerySessionLog → spawn_sub_agent → 循环
///   · 透明协议 — 查询协议内嵌在子代理 prompt 中，对主 Agent 完全透明
/// </summary>
[Tool(
    id: "smart_query_session_log",
    name: "Smart Query Session Log",
    description: "智能会话日志查询。用自然语言描述查询意图（what），" +
                 "内部使用子代理自动调用 query_session_logs 搜索会话历史，" +
                 "自动设置 exclude_heartbeat=true 以过滤心跳噪音并分页遍历。" +
                 "一次调用完成 query_session_logs 的 grep + messages 多轮翻阅。" +
                 "参数：what（查询什么，自然语言描述，如 '找到 turnId=2529 的任务用了哪些工具'）、" +
                 "session_id（可选，指定会话ID）、max_results（可选，默认 10）、" +
                 "model（可选，推荐 deepseek-v4-flash——日志查询是低推理任务）、" +
                 "timeout_seconds（可选，默认 90s，大范围搜索可传入更大值）。" +
                 "无需关心 query_session_logs 的复杂参数和翻页逻辑。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartQuerySessionLogsTool : PuddingToolBase<SmartQuerySessionLogsArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly ILogger<SmartQuerySessionLogsTool> _logger;

    private const string SubAgentTemplateId = "workspace-task-agent";
    private const int DefaultMaxResults = 10;
    private const int DefaultMaxRounds = 12;

    public SmartQuerySessionLogsTool(
        IServiceProvider serviceProvider,
        IRuntimeActivitySink activitySink,
        ILogger<SmartQuerySessionLogsTool> logger)
    {
        _serviceProvider = serviceProvider;
        _activitySink = activitySink;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartQuerySessionLogsArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var what = args.What?.Trim();
        if (string.IsNullOrWhiteSpace(what))
            return ToolExecutionResult.Fail("what is required. Describe what you want to find in session logs in natural language.");

        var maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 20);

        var modelUsed = args.Model ?? "(default)";
        var timeoutUsed = args.TimeoutSeconds ?? 90;
        var capabilityUsed = args.CapabilityRequirements ?? "(none)";

        _logger.LogInformation(
            "[SmartQuerySessionLog] agent={Agent} what={What} sessionId={SessionId} model={Model} timeout={Timeout}s capability={Capability}",
            context.AgentInstanceId, what, args.SessionId ?? "(any)",
            modelUsed, timeoutUsed, capabilityUsed);

        var sw = Stopwatch.StartNew();
        var activityId = Guid.NewGuid().ToString("N");
        var subAgentId = "(unknown)";

        var task = BuildQueryTask(what, args.SessionId, maxResults);

        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: context.SessionId,
                workspaceId: context.WorkspaceId,
                correlationId: activityId),
            Component = RuntimeActivityComponents.SmartToolWrapper,
            Operation = "smart_query_session_log",
            Status = RuntimeActivityStatuses.Started,
            Summary = $"SmartQuerySessionLog: {Truncate(what)}",
            Metadata = new Dictionary<string, string>
            {
                ["tool_name"] = "smart_query_session_log",
                ["what"] = Truncate(what),
                ["session_id"] = context.SessionId,
                ["target_session_id"] = args.SessionId ?? "(any)",
                ["parent_correlation_id"] = activityId,
                ["model"] = modelUsed,
                ["timeout_seconds"] = timeoutUsed.ToString(),
                ["capability"] = capabilityUsed,
            },
        }, ct);

        try
        {
            var spawnArgs = JsonSerializer.Serialize(new
            {
                task,
                agent_template = SubAgentTemplateId,
                sync = true,
                model = args.Model,
                capability_requirements = args.CapabilityRequirements,
                timeout_seconds = timeoutUsed,
                max_rounds = DefaultMaxRounds,
                allow_sub_delegation = false,
            });

            _logger.LogInformation(
                "[SmartQuerySessionLog] agent={Agent} spawning sub-agent model={Model} timeout={Timeout}s",
                context.AgentInstanceId, modelUsed, timeoutUsed);

            var toolExec = _serviceProvider.GetRequiredService<IPuddingToolExecutionService>();
            var result = await toolExec.ExecuteAsync(
                "spawn_sub_agent",
                spawnArgs,
                context,
                null,
                ct);

            sw.Stop();
            var outputSummary = result.Success
                ? SummarizeOutput(result.Output, 200)
                : $"FAILED: {Truncate(result.Error ?? "unknown", 200)}";
            ExtractSubAgentId(result.Output, ref subAgentId);

            if (result.Success)
            {
                await _activitySink.RecordAsync(new RuntimeActivity
                {
                    Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                    Component = RuntimeActivityComponents.SmartToolWrapper,
                    Operation = "smart_query_session_log",
                    Status = RuntimeActivityStatuses.Succeeded,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    EndedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = sw.ElapsedMilliseconds,
                    Summary = $"SmartQuerySessionLog: {outputSummary}",
                    Metadata = new Dictionary<string, string>
                    {
                        ["duration_ms"] = sw.ElapsedMilliseconds.ToString(),
                        ["session_id"] = context.SessionId,
                        ["parent_correlation_id"] = activityId,
                        ["sub_agent_id"] = subAgentId,
                        ["model"] = modelUsed,
                        ["output_chars"] = (result.Output?.Length ?? 0).ToString(),
                    },
                }, ct);
                _logger.LogInformation(
                    "[SmartQuerySessionLog] agent={Agent} SUCCESS subAgent={SubAgent} duration={Duration}ms output={OutputLen} chars summary={Summary}",
                    context.AgentInstanceId, subAgentId, sw.ElapsedMilliseconds,
                    result.Output?.Length ?? 0, outputSummary);
                return ToolExecutionResult.Ok(result.Output);
            }

            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                Component = RuntimeActivityComponents.SmartToolWrapper,
                Operation = "smart_query_session_log",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartQuerySessionLog: {outputSummary}",
                ErrorMessage = result.Error,
                Metadata = new Dictionary<string, string>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds.ToString(),
                    ["error"] = result.Error ?? "unknown",
                    ["session_id"] = context.SessionId,
                    ["parent_correlation_id"] = activityId,
                    ["sub_agent_id"] = subAgentId,
                    ["model"] = modelUsed,
                    ["capability"] = capabilityUsed,
                },
            }, ct);
            var failMsg = BuildFailureMessage("SmartQuerySessionLog", result.Error, activityId, modelUsed,
                timeoutUsed, capabilityUsed, args.SessionId, null);
            _logger.LogError(
                "[SmartQuerySessionLog] agent={Agent} FAILED subAgent={SubAgent} duration={Duration}ms error={Error}",
                context.AgentInstanceId, subAgentId, sw.ElapsedMilliseconds, result.Error ?? "unknown");
            return ToolExecutionResult.Fail(failMsg);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                Component = RuntimeActivityComponents.SmartToolWrapper,
                Operation = "smart_query_session_log",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartQuerySessionLog: TIMEOUT after {sw.ElapsedMilliseconds}ms (limit={timeoutUsed}s)",
                ErrorMessage = "TaskCanceledException",
                Metadata = new Dictionary<string, string>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds.ToString(),
                    ["error"] = "timeout",
                    ["session_id"] = context.SessionId,
                    ["parent_correlation_id"] = activityId,
                    ["sub_agent_id"] = subAgentId,
                    ["model"] = modelUsed,
                    ["timeout_limit_s"] = timeoutUsed.ToString(),
                    ["capability"] = capabilityUsed,
                },
            }, ct);
            var timeoutMsg = $"SmartQuerySessionLog TIMED OUT after {sw.ElapsedMilliseconds}ms " +
                $"(limit={timeoutUsed}s, model={modelUsed}, capability={capabilityUsed}). " +
                $"Try narrower query or increase timeout_seconds. " +
                $"[correlation_id={activityId}]";
            _logger.LogError(
                "[SmartQuerySessionLog] agent={Agent} TIMEOUT duration={Duration}ms limit={Limit}s model={Model}",
                context.AgentInstanceId, sw.ElapsedMilliseconds, timeoutUsed, modelUsed);
            return ToolExecutionResult.Fail(timeoutMsg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[SmartQuerySessionLog] agent={Agent} error", context.AgentInstanceId);
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                Component = RuntimeActivityComponents.SmartToolWrapper,
                Operation = "smart_query_session_log",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartQuerySessionLog: EXCEPTION {ex.GetType().Name}",
                ErrorMessage = ex.Message,
                Metadata = new Dictionary<string, string>
                {
                    ["duration_ms"] = sw.ElapsedMilliseconds.ToString(),
                    ["error"] = ex.GetType().Name,
                    ["session_id"] = context.SessionId,
                    ["parent_correlation_id"] = activityId,
                    ["sub_agent_id"] = subAgentId,
                    ["model"] = modelUsed,
                    ["exception_type"] = ex.GetType().FullName ?? ex.GetType().Name,
                    ["exception_message"] = ex.Message,
                },
            }, ct);
            return ToolExecutionResult.Fail(
                $"SmartQuerySessionLog EXCEPTION: {ex.GetType().Name}: {ex.Message}. " +
                $"[correlation_id={activityId}]");
        }
    }

    /// <summary>
    /// 构建子代理查询任务 prompt。
    /// 分为三层：
    ///   Layer 1 — 查询协议（对主 Agent 透明，子代理强制遵守）
    ///   Layer 2 — 用户查询（透传 what/sessionId）
    ///   Layer 3 — 输出格式约束（含 FOUND / MISSING 退路）
    /// </summary>
    private static string BuildQueryTask(string what, string? sessionId, int maxResults)
    {
        var sb = new StringBuilder();

        // ═══════════════════════════════════════════════════════════
        // Layer 1: 查询协议（对主 Agent 透明，子代理强制遵守）
        // ═══════════════════════════════════════════════════════════
        sb.AppendLine("## 🔍 SmartQuerySessionLog Protocol (MUST FOLLOW — DO NOT SKIP STEPS)");
        sb.AppendLine();
        sb.AppendLine("You are a session-log query sub-agent. Your ONLY job is to find");
        sb.AppendLine("relevant conversation history. Do NOT engage in conversation.");
        sb.AppendLine("Do NOT give up early. Exhaust ALL query strategies before concluding.");
        sb.AppendLine();
        sb.AppendLine("⚡ This is a LOG QUERY task — NOT a reasoning task.");
        sb.AppendLine("   Find conversation history and return results. Do NOT over-think.");
        sb.AppendLine("   Speed matters. Use tools directly, minimize reflection between calls.");
        sb.AppendLine();

        // Phase 1: Broad discovery
        sb.AppendLine("### PHASE 1 — Broad Discovery (MANDATORY)");
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sb.AppendLine("1a. You have a specific session ID. Start with `query_session_logs` action=grep");
            sb.AppendLine("    on that session to locate relevant messages.");
            sb.AppendLine("    ALWAYS use exclude_heartbeat=true.");
        }
        else
        {
            sb.AppendLine("1a. No session ID specified. First, use `query_session_logs` action=list_sessions");
            sb.AppendLine("    or action=list_days to find candidate sessions within the relevant time range.");
            sb.AppendLine("1b. Use `query_session_logs` action=grep with keywords from the query intent.");
            sb.AppendLine("    Try MULTIPLE keyword variations. ALWAYS set exclude_heartbeat=true.");
            sb.AppendLine("1c. Identify the most promising 2-3 sessions from grep results.");
        }
        sb.AppendLine($"Target: identify at least {Math.Min(maxResults, 3)} candidate sessions or message clusters.");
        sb.AppendLine();

        // Phase 2: Deep read
        sb.AppendLine("### PHASE 2 — Deep Reading (MANDATORY)");
        sb.AppendLine("2a. For each candidate session, call `query_session_logs` action=messages");
        sb.AppendLine("    with exclude_heartbeat=true to read the transcript.");
        sb.AppendLine("2b. If the transcript spans multiple pages, paginate (page=2, page=3...)");
        sb.AppendLine("    until you have enough context or reach the end.");
        sb.AppendLine("2c. Extract the relevant conversation segments that answer the query intent.");
        sb.AppendLine("2d. Focus on: user messages, agent responses, tool call results.");
        sb.AppendLine("    Skip: heartbeat messages, system noise, empty turns.");
        sb.AppendLine();

        // Phase 3: Fallback
        sb.AppendLine("### PHASE 3 — Fallback (ONLY if Phases 1-2 found NOTHING or too little)");
        sb.AppendLine("3a. **Broader grep**: use shorter keywords, partial terms, synonyms.");
        sb.AppendLine("3b. **Wider time range**: search adjacent days/sessions.");
        sb.AppendLine("3c. **By message_id**: if the query references a specific message ID or turn ID,");
        sb.AppendLine("    use action=messages with message_id or grep on raw events.");
        sb.AppendLine("3d. If still nothing after exhausting ALL options:");
        sb.AppendLine("    - Report FOUND=no");
        sb.AppendLine("    - In MISSING section: list EXACTLY which sessions/keywords/strategies you tried");
        sb.AppendLine("    - Suggest 2-3 alternative queries the main agent could try next");
        sb.AppendLine("    - NEVER return FOUND=no without listing what you attempted");
        sb.AppendLine();

        // Tool restrictions
        sb.AppendLine("### ⚠️ TOOL RESTRICTIONS");
        sb.AppendLine("- Primary tool: query_session_logs (actions: grep, messages, list_sessions, list_days, read_raw_events).");
        sb.AppendLine("- ALWAYS use exclude_heartbeat=true to filter noise.");
        sb.AppendLine("- Do NOT use: smart_query_session_log, smart_search, spawn_sub_agent, shell, file_write.");
        sb.AppendLine("- You ARE the query agent — do not delegate.");
        sb.AppendLine();

        // ═══════════════════════════════════════════════════════════
        // Layer 2: 用户查询（透传）
        // ═══════════════════════════════════════════════════════════
        sb.AppendLine("## 🎯 Query Intent");
        sb.AppendLine(what);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            sb.AppendLine("### 📋 Session");
            sb.AppendLine($"Target session ID: `{sessionId}`");
            sb.AppendLine("Start with this session. If not found, fall back to Phase 3 broader search.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("### 📋 Session");
            sb.AppendLine("No specific session ID provided. Search across all available sessions.");
            sb.AppendLine();
        }

        // ═══════════════════════════════════════════════════════════
        // Layer 3: 输出格式（含退路）
        // ═══════════════════════════════════════════════════════════
        sb.AppendLine("## 📋 Output Format (REQUIRED — use this exact structure)");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("FOUND: yes | partial | no");
        sb.AppendLine();
        sb.AppendLine("SESSION_IDS:");
        sb.AppendLine("  - session_id_1: brief description of what this session contains");
        sb.AppendLine("  - session_id_2: brief description");
        sb.AppendLine();
        sb.AppendLine("FINDINGS:");
        sb.AppendLine("  - [timestamp / turn] role: key content (source: session_id)");
        sb.AppendLine("  - [timestamp / turn] role: key content (source: session_id)");
        sb.AppendLine();
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  2-4 sentence summary that directly answers the original query intent.");
        sb.AppendLine("  If FOUND=no: explain why, and list all strategies/keywords/sessions tried.");
        sb.AppendLine();
        sb.AppendLine("MISSING:");
        sb.AppendLine("  - What was NOT found that might still be relevant?");
        sb.AppendLine("  - Suggest 2-3 alternative queries, different keywords, or time ranges");
        sb.AppendLine("    the main agent could try in a follow-up smart_query_session_log call.");
        sb.AppendLine("  - If FOUND=yes and confident, write 'Nothing significant missing.'");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Target: {maxResults} findings maximum. Prefer fewer high-quality findings over many noisy ones.");
        sb.AppendLine("Begin querying NOW. Do not reply with anything other than the search results.");

        return sb.ToString();
    }

    /// <summary>从子代理输出中提取 summary（前 N 个字符）。</summary>
    private static string SummarizeOutput(string? output, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "(empty output)";

        // Try to extract FOUND line
        var foundLine = output.Split('\n')
            .FirstOrDefault(l => l.TrimStart().StartsWith("FOUND:"));
        var prefix = foundLine?.Trim() ?? "";

        // Try to extract SUMMARY paragraph
        var summaryStart = output.IndexOf("SUMMARY:", StringComparison.Ordinal);
        var summary = "";
        if (summaryStart >= 0)
        {
            var end = output.IndexOf("\n\n", summaryStart, StringComparison.Ordinal);
            if (end < 0) end = Math.Min(summaryStart + 300, output.Length);
            summary = output[summaryStart..end].Replace("\n", " ").Trim();
        }

        var combined = string.IsNullOrEmpty(summary)
            ? prefix
            : $"{prefix} {summary}";

        return Truncate(combined, maxLen);
    }

    /// <summary>尝试从子代理输出或错误中提取 sub_agent_id。</summary>
    private static void ExtractSubAgentId(string? output, ref string subAgentId)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("sub-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("sub_", StringComparison.OrdinalIgnoreCase))
            {
                var idx = trimmed.IndexOf("sub-", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = trimmed.IndexOf("sub_", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var end = idx + 4;
                    while (end < trimmed.Length &&
                           (char.IsLetterOrDigit(trimmed[end]) || trimmed[end] == '-' || trimmed[end] == '_'))
                        end++;
                    var candidate = trimmed[idx..end];
                    if (candidate.Length >= 8)
                    {
                        subAgentId = candidate;
                        return;
                    }
                }
            }
        }
    }

    /// <summary>构建带诊断信息的失败消息。</summary>
    private static string BuildFailureMessage(
        string toolName, string? error, string correlationId,
        string model, int timeout, string capability,
        string? sessionId, string? _)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"❌ {toolName} sub-agent FAILED.");
        if (!string.IsNullOrWhiteSpace(error))
            sb.AppendLine($"   Error: {error}");

        sb.AppendLine($"   ── Diagnostics ──");
        sb.AppendLine($"   correlation_id: {correlationId}");
        sb.AppendLine($"   model: {model}");
        sb.AppendLine($"   timeout: {timeout}s");
        sb.AppendLine($"   capability: {capability}");
        if (!string.IsNullOrWhiteSpace(sessionId)) sb.AppendLine($"   session_id: {sessionId}");
        sb.Append($"   💡 Tip: grep session logs with correlation_id to trace sub-agent internals.");
        return sb.ToString();
    }

    private static string Truncate(string s, int maxLen = 80)
        => s.Length <= maxLen ? s : s[..(maxLen - 3)] + "...";
}

/// <summary>SmartQuerySessionLog 工具参数 — 全部自然语言，无需理解 query_session_logs 的复杂参数。</summary>
public sealed class SmartQuerySessionLogsArgs
{
    [ToolParam("查询什么 — 自然语言描述查询意图，如 '找到 turnId=2529 的任务用了哪些工具' 或 '搜索讨论过记忆系统设计的会话'")]
    public string? What { get; set; }

    [ToolParam("指定会话 ID（可选），如 'cfa35d8c6ee04c9580298c9086ce72df'")]
    public string? SessionId { get; set; }

    [ToolParam("最多返回多少结果，1-20，默认 10")]
    public int? MaxResults { get; set; }

    [ToolParam("指定子代理模型（可选），透传给 spawn_sub_agent。推荐 deepseek-v4-flash——日志查询是低推理任务，flash 模型更快更省。不指定则使用平台默认。")]
    public string? Model { get; set; }

    /// <summary>子代理超时秒数（可选），默认 90s。大范围日志搜索需要更长时间。</summary>
    [ToolParam("子代理超时秒数（可选），默认 90s。大范围日志搜索需要更长时间，可传入更大的值。")]
    public int? TimeoutSeconds { get; set; }

    [ToolParam("能力需求标签（可选），如 'fast,retrieval'。系统自动从 LLM 资源池选择匹配能力的模型。不指定则使用 model 参数或平台默认。")]
    public string? CapabilityRequirements { get; set; }

}
