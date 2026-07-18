using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Models;
using PuddingCode.Observability;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingCode.Agents;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// SmartSearchTool — 语义化代码搜索。主 Agent 使用自然语言描述"找什么"，
/// 内部委托子代理（deepseek-v4-flash 模型）执行多轮 file_search + search_grep + file_read，
/// 返回带上下文的摘要结果。一次调用替代原本需要 3-6 轮的搜索任务。
///
/// 设计原则：
///   · Agent = Function — 工具签名即自然语言，Agent 不需要理解子代理协议
///   · 薄包装 — 核心逻辑在子代理模板中，本工具只做参数映射和结果整理
///   · MainAgentOnly — 不暴露给子代理，防止 SmartSearch → spawn_sub_agent → SmartSearch 循环
///   · 透明协议 — 搜索协议内嵌在子代理 prompt 中，对主 Agent 完全透明
/// </summary>
[Tool(
    id: "smart_search",
    name: "Smart Search",
    description: "【已合并到 smart_explore】智能代码搜索。用自然语言描述搜索意图（what/scope/focus），" +
                 "内部使用子代理自动执行多轮搜索，返回带上下文的摘要结果。" +
                 "一次调用完成 file_search + 多次 file_read 的工作。" +
                 "参数：what（搜索什么，自然语言描述）、scope（可选，目录范围）、" +
                 "focus（可选，重点关注哪些方面）、max_results（可选，默认 10）、" +
                 "model（可选，推荐 deepseek-v4-flash——搜索是低推理任务，flash 模型更快更省）、" +
                 "timeout_seconds（可选，默认 120s，大仓库搜索可传入更大值）。" +
                 "无需关心实现细节和子代理协议。",
    category: ToolCategory.Query,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.ReadOnly | ToolSafetyFlags.ConcurrencySafe,
    SubAgentExposure = SubAgentExposure.MainAgentOnly)]
public sealed class SmartSearchTool : PuddingToolBase<SmartSearchArgs>
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRuntimeActivitySink _activitySink;
    private readonly ILogger<SmartSearchTool> _logger;

    private const string SubAgentTemplateId = "workspace-task-agent";
    private const int DefaultMaxResults = 10;
    private const int DefaultMaxRounds = 12;

    public SmartSearchTool(
        IServiceProvider serviceProvider,
        IRuntimeActivitySink activitySink,
        ILogger<SmartSearchTool> logger)
    {
        _serviceProvider = serviceProvider;
        _activitySink = activitySink;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SmartSearchArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var what = args.What?.Trim();
        if (string.IsNullOrWhiteSpace(what))
            return ToolExecutionResult.Fail("what is required. Describe what you want to search for in natural language.");

        var maxResults = Math.Clamp(args.MaxResults ?? DefaultMaxResults, 1, 20);

        var modelUsed = args.Model ?? (string.IsNullOrWhiteSpace(args.CapabilityRequirements) ? "(default)" : $"(capability: {args.CapabilityRequirements})");
        var timeoutUsed = args.TimeoutSeconds ?? 120;
        var capabilityUsed = args.CapabilityRequirements ?? "(none)";

        _logger.LogInformation(
            "[SmartSearch] agent={Agent} what={What} scope={Scope} model={Model} timeout={Timeout}s capability={Capability}",
            context.AgentInstanceId, what, args.Scope ?? "(root)",
            modelUsed, timeoutUsed, capabilityUsed);

        var sw = Stopwatch.StartNew();
        var activityId = Guid.NewGuid().ToString("N");
        var subAgentId = "(unknown)";

        var task = BuildSearchTask(what, args.Scope, args.Focus, maxResults);

        // Record start
        await _activitySink.RecordAsync(new RuntimeActivity
        {
            Trace = RuntimeTraceContext.CreateNew(
                sessionId: context.SessionId,
                workspaceId: context.WorkspaceId,
                correlationId: activityId),
            Component = RuntimeActivityComponents.SmartToolWrapper,
            Operation = "smart_search",
            Status = RuntimeActivityStatuses.Started,
            Summary = $"SmartSearch: {TruncateWhat(what)}",
            Metadata = new Dictionary<string, string>
            {
                ["tool_name"] = "smart_search",
                ["what"] = TruncateWhat(what),
                ["scope"] = args.Scope ?? "(root)",
                ["session_id"] = context.SessionId,
                ["parent_correlation_id"] = activityId,
                ["model"] = modelUsed,
                ["timeout_seconds"] = timeoutUsed.ToString(),
                ["capability"] = capabilityUsed,
            },
        }, ct);

                try
        {
            // Explorer 模型解析：无显式 model 时从父 Agent manifest 读取
            var resolvedModel = args.Model;
            if (string.IsNullOrWhiteSpace(resolvedModel))
            {
                try
                {
                    var profileProvider = _serviceProvider.GetService<AgentProfileProvider>();
                    if (profileProvider is not null)
                    {
                        var profile = await profileProvider.LoadAsync(context.AgentInstanceId, CancellationToken.None);
                        resolvedModel = profile.Instance.ExplorerModel;
                        if (!string.IsNullOrWhiteSpace(resolvedModel))
                            _logger.LogInformation("[SmartSearch] Resolved explorer model: {Model}", resolvedModel);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[SmartSearch] Failed to resolve explorer model");
                }
            }

            var spawnArgs = JsonSerializer.Serialize(new
            {
                task,
                agent_template = SubAgentTemplateId,
                sync = true,
                model = resolvedModel,
                role_in_plan = "explorer",
                capability_requirements = args.CapabilityRequirements,
                timeout_seconds = timeoutUsed,
                max_rounds = DefaultMaxRounds,
                allow_sub_delegation = false,
            });

            _logger.LogInformation(
                "[SmartSearch] agent={Agent} spawning sub-agent model={Model} timeout={Timeout}s",
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
                : $"FAILED: {TruncateWhat(result.Error ?? "unknown", 200)}";
            ExtractSubAgentId(result.Output, ref subAgentId);

            if (result.Success)
            {
                await _activitySink.RecordAsync(new RuntimeActivity
                {
                    Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                    Component = RuntimeActivityComponents.SmartToolWrapper,
                    Operation = "smart_search",
                    Status = RuntimeActivityStatuses.Succeeded,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    EndedAtUtc = DateTimeOffset.UtcNow,
                    DurationMs = sw.ElapsedMilliseconds,
                    Summary = $"SmartSearch: {outputSummary}",
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
                    "[SmartSearch] agent={Agent} SUCCESS subAgent={SubAgent} duration={Duration}ms output={OutputLen} chars summary={Summary}",
                    context.AgentInstanceId, subAgentId, sw.ElapsedMilliseconds,
                    result.Output?.Length ?? 0, outputSummary);
                return ToolExecutionResult.Ok(result.Output);
            }

            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                Component = RuntimeActivityComponents.SmartToolWrapper,
                Operation = "smart_search",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartSearch: {outputSummary}",
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
            var failMsg = BuildFailureMessage("SmartSearch", result.Error, activityId, modelUsed,
                timeoutUsed, capabilityUsed, args.Scope, args.Focus);
            _logger.LogError(
                "[SmartSearch] agent={Agent} FAILED subAgent={SubAgent} duration={Duration}ms error={Error}",
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
                Operation = "smart_search",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartSearch: TIMEOUT after {sw.ElapsedMilliseconds}ms (limit={timeoutUsed}s)",
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
            var timeoutMsg = $"SmartSearch TIMED OUT after {sw.ElapsedMilliseconds}ms " +
                $"(limit={timeoutUsed}s, model={modelUsed}, capability={capabilityUsed}). " +
                $"Try narrower scope, more specific query, or increase timeout_seconds. " +
                $"[correlation_id={activityId}]";
            _logger.LogError(
                "[SmartSearch] agent={Agent} TIMEOUT duration={Duration}ms limit={Limit}s model={Model}",
                context.AgentInstanceId, sw.ElapsedMilliseconds, timeoutUsed, modelUsed);
            return ToolExecutionResult.Fail(timeoutMsg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "[SmartSearch] agent={Agent} error", context.AgentInstanceId);
            await _activitySink.RecordAsync(new RuntimeActivity
            {
                Trace = RuntimeTraceContext.CreateNew(correlationId: activityId),
                Component = RuntimeActivityComponents.SmartToolWrapper,
                Operation = "smart_search",
                Status = RuntimeActivityStatuses.Failed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                EndedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = sw.ElapsedMilliseconds,
                Summary = $"SmartSearch: EXCEPTION {ex.GetType().Name}",
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
                $"SmartSearch EXCEPTION: {ex.GetType().Name}: {ex.Message}. " +
                $"[correlation_id={activityId}]");
        }
    }

    /// <summary>
    /// 构建子代理搜索任务 prompt。
    /// 分为三层：
    ///   Layer 1 — 搜索协议（对主 Agent 透明，子代理强制遵守）
    ///   Layer 2 — 用户查询（透传 what/scope/focus）
    ///   Layer 3 — 输出格式约束（含 FOUND / MISSING 退路）
    /// </summary>
    private static string BuildSearchTask(string what, string? scope, string? focus, int maxResults)
    {
        var sb = new StringBuilder();

        // ═══════════════════════════════════════════════════════════
        // Layer 1: 搜索协议（对主 Agent 透明，子代理强制遵守）
        // ═══════════════════════════════════════════════════════════
        sb.AppendLine("## 🔍 SmartSearch Protocol (MUST FOLLOW — DO NOT SKIP STEPS)");
        sb.AppendLine();
        sb.AppendLine("You are a search sub-agent. Your ONLY job is to find information.");
        sb.AppendLine("Do NOT engage in conversation. Do NOT ask clarifying questions.");
        sb.AppendLine("Do NOT give up early. Exhaust ALL tools before concluding.");
        sb.AppendLine();
        sb.AppendLine("⚡ This is a SEARCH/RETRIEVAL task — NOT a reasoning task.");
        sb.AppendLine("   Find information and return results. Do NOT over-think or analyze deeply.");
        sb.AppendLine("   Speed matters. Use tools directly, minimize reflection between calls.");
        sb.AppendLine();

        // Phase 1: Broad discovery
        sb.AppendLine("### PHASE 1 — Broad Discovery (MANDATORY)");
        sb.AppendLine("1a. Call `file_search` with relevant patterns derived from the search target.");
        sb.AppendLine("    Try MULTIPLE patterns if the first yields nothing.");
        sb.AppendLine("    Examples: `*Tool*.cs`, `*Search*.cs`, `*Handler*.cs`, `*Service*.cs`.");
        sb.AppendLine($"    Target: find at least {Math.Min(maxResults, 5)} candidate files.");
        sb.AppendLine("1b. Call `search_grep` on the scope directory with keywords from the search target.");
        sb.AppendLine("    Try MULTIPLE keyword variations.");
        sb.AppendLine("    Examples: class name, method name, key phrases, camelCase/snake_case variants.");
        sb.AppendLine("1c. Merge deduplicated results from 1a and 1b into a candidate list.");
        sb.AppendLine("    Sort by relevance: exact name match > partial name match > content match.");
        sb.AppendLine();

        // Phase 2: Deep read
        sb.AppendLine("### PHASE 2 — Deep Reading (MANDATORY)");
        sb.AppendLine("2a. For each high-confidence candidate (top candidates first),");
        sb.AppendLine("    call `file_read` with head_lines/tail_lines to confirm relevance.");
        sb.AppendLine("    Read just enough context — NOT the whole file unless it is small.");
        sb.AppendLine("2b. If Focus Areas are specified below, use `search_grep` WITHIN");
        sb.AppendLine("    each candidate file to locate the specific sections of interest.");
        sb.AppendLine("2c. For confirmed matches, read the surrounding context (10-30 lines).");
        sb.AppendLine($"2d. Stop when you have {maxResults} high-quality results or all candidates exhausted.");
        sb.AppendLine();

        // Phase 3: Fallback
        sb.AppendLine("### PHASE 3 — Fallback (ONLY if Phases 1-2 found NOTHING or too little)");
        sb.AppendLine("3a. **Broaden patterns**: use shorter names, wildcards, parent directories.");
        sb.AppendLine("3b. **Broader keywords**: search for parent types, interfaces, base classes.");
        sb.AppendLine("3c. **List directory**: use `list_dir` on the scope to understand what files exist.");
        sb.AppendLine("3d. **Search wider**: expand scope to parent/sibling directories.");
        sb.AppendLine("3e. If still nothing after exhausting ALL options:");
        sb.AppendLine("    - Report FOUND=no");
        sb.AppendLine("    - In MISSING section: list EXACTLY which tools/patterns/keywords you tried");
        sb.AppendLine("    - Suggest 2-3 alternative search strategies the main agent could try next");
        sb.AppendLine("    - NEVER return FOUND=no without listing what you attempted");
        sb.AppendLine();

        // Tool restrictions
        sb.AppendLine("### ⚠️ TOOL RESTRICTIONS");
        sb.AppendLine("- Available tools: file_search, search_grep, file_read, list_dir.");
        sb.AppendLine("- Do NOT use: smart_search, spawn_sub_agent, shell, file_write, any write tools.");
        sb.AppendLine("- You ARE the search agent — do not delegate.");
        sb.AppendLine();

        // ═══════════════════════════════════════════════════════════
        // Layer 2: 用户查询（透传）
        // ═══════════════════════════════════════════════════════════
        sb.AppendLine("## 🎯 Search Target");
        sb.AppendLine(what);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(scope))
        {
            sb.AppendLine("### 📁 Scope");
            sb.AppendLine($"Search within: `{scope}`");
            sb.AppendLine("If nothing is found, try expanding to parent directories in Phase 3.");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("### 📁 Scope");
            sb.AppendLine("Workspace root. Start broad, then narrow. If results are too noisy, focus on likely directories.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(focus))
        {
            sb.AppendLine("### 🔬 Focus Areas");
            sb.AppendLine($"Prioritize findings related to: {focus}");
            sb.AppendLine("Use search_grep WITHIN each candidate to locate these specific aspects.");
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
        sb.AppendLine("FILES:");
        sb.AppendLine("  - path/to/file.cs (lines 45-78): one-line description of relevance");
        sb.AppendLine("  - path/to/other.cs (line 123): one-line description");
        sb.AppendLine();
        sb.AppendLine("SUMMARY:");
        sb.AppendLine("  2-4 sentence summary of what was found and its significance.");
        sb.AppendLine("  If FOUND=no: explain why, and list all tools/patterns/keywords tried.");
        sb.AppendLine();
        sb.AppendLine("CODE:");
        sb.AppendLine("  ```lang");
        sb.AppendLine("  // Relevant code snippets if found. Keep it brief (max 30 lines total).");
        sb.AppendLine("  // Include line numbers as comments if helpful.");
        sb.AppendLine("  ```");
        sb.AppendLine("  (omit this section if FOUND=no)");
        sb.AppendLine();
        sb.AppendLine("MISSING:");
        sb.AppendLine("  - List what was NOT found that might still be relevant.");
        sb.AppendLine("  - Suggest 2-3 alternative search terms, broader scopes, or different");
        sb.AppendLine("    approaches the main agent could try in a follow-up smart_search call.");
        sb.AppendLine("  - If FOUND=yes and you are confident, write 'Nothing significant missing.'");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"Target: {maxResults} results maximum. Prefer fewer high-quality results over many noisy ones.");
        sb.AppendLine("Begin searching NOW. Do not reply with anything other than the search results.");

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

        return TruncateWhat(combined, maxLen);
    }

    /// <summary>尝试从子代理输出或错误中提取 sub_agent_id。</summary>
    private static void ExtractSubAgentId(string? output, ref string subAgentId)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        // Look for patterns like "sub-xxxxxxxx" or "sub_xxxxxxxx"
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("sub-", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("sub_", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract the sub-agent ID
                var idx = trimmed.IndexOf("sub-", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) idx = trimmed.IndexOf("sub_", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var end = idx + 4; // "sub-" or "sub_"
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
        string? scope, string? focus)
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
        if (!string.IsNullOrWhiteSpace(scope)) sb.AppendLine($"   scope: {scope}");
        if (!string.IsNullOrWhiteSpace(focus)) sb.AppendLine($"   focus: {focus}");
        sb.Append($"   💡 Tip: grep session logs with correlation_id to trace sub-agent internals.");
        return sb.ToString();
    }

    private static string TruncateWhat(string what, int maxLen = 80)
        => what.Length <= maxLen ? what : what[..(maxLen - 3)] + "...";
}

/// <summary>SmartSearch 工具参数 — 全部自然语言，无需理解子代理协议。</summary>
public sealed class SmartSearchArgs
{
    /// <summary>搜索什么 — 自然语言描述搜索目标。</summary>
    [ToolParam("搜索什么 — 自然语言描述搜索目标，如 '找到 FileSearchTool 的超时处理逻辑'")]
    public string? What { get; set; }

    /// <summary>搜索范围目录 — 相对于工作区根目录。</summary>
    [ToolParam("搜索范围目录，相对于工作区根目录，如 'Source/PuddingRuntime/Tools'")]
    public string? Scope { get; set; }

    /// <summary>重点关注哪些方面。</summary>
    [ToolParam("重点关注哪些方面，如 '超时处理、CancellationToken 传递'")]
    public string? Focus { get; set; }

    /// <summary>最多返回多少结果（1-20，默认 10）。</summary>
    [ToolParam("最多返回多少结果，1-20，默认 10")]
    public int? MaxResults { get; set; }

    /// <summary>指定子代理模型（可选），透传给 spawn_sub_agent。不指定则使用平台默认。</summary>
    [ToolParam("指定子代理模型（可选），透传给 spawn_sub_agent。推荐 deepseek-v4-flash——搜索是低推理任务，flash 模型更快更省。不指定则使用平台默认。")]
    public string? Model { get; set; }

    /// <summary>子代理超时秒数（可选），默认 120s。大仓库搜索需要更长时间。</summary>
    [ToolParam("子代理超时秒数（可选），默认 120s。大仓库搜索需要更长时间，可传入更大的值。")]
    public int? TimeoutSeconds { get; set; }

    [ToolParam("能力需求标签（可选），如 'fast,search'。系统自动从 LLM 资源池选择匹配能力的模型。不指定则使用 model 参数或平台默认。")]
    public string? CapabilityRequirements { get; set; }

}
