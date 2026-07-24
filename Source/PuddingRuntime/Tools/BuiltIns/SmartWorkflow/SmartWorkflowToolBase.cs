using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PuddingCode.Agents;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services.AgentLoop;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Smart 工作流工具基类 — 提取公共子代理调用逻辑。
///
/// 设计原则：
///   · Agent = Function — 工具签名即自然语言
///   · 薄包装 — 核心逻辑在 role_in_plan 驱动的子代理中
///   · 有界委派 — 仅显式白名单 Smart 边可委派，深度由 Runtime 强制限制
///   · 模型选择 — 通过 role_in_plan → manifest.{Role}Model 推导
/// </summary>
public abstract class SmartWorkflowToolBase<TArgs> : PuddingToolBase<TArgs> where TArgs : class, new()
{
    protected const string SubAgentTemplateId = "workspace-task-agent";
    protected const int SmartWorkflowTimeoutSeconds = 60 * 60;
    protected const int DefaultParentFinalizationReserveSeconds = 2 * 60;
    protected abstract string RoleName { get; }
    protected abstract string BuildTaskPrompt(TArgs args, ToolExecutionContext context);
    protected virtual int DefaultTimeoutSeconds => SmartWorkflowTimeoutSeconds;
    protected virtual int DefaultMaxRounds => 15;
    /// <summary>子代理允许的工具列表，逗号分隔。null = 继承父代理全部工具。</summary>
    protected virtual string? AllowedTools => null;
    /// <summary>主模型失败时的降级模型 ID 列表（按优先级）。null = 不启用 fallback。</summary>
    protected virtual IReadOnlyList<string>? FallbackModelIds => null;
    /// <summary>仅由明确设计为 DAG 父节点的 Smart 工具覆盖为 true。</summary>
    protected virtual bool AllowNestedSmartDelegation => false;
    protected virtual int MaxDelegationDepth => 2;

    /// <summary>
    /// Appends the canonical report rules understood by <c>spawn_sub_agent</c>.
    /// Role prompts must add their role-specific fields under these five sections.
    /// </summary>
    protected static void AppendCanonicalReportRules(StringBuilder sb)
    {
        sb.AppendLine("### REQUIRED WORK REPORT");
        sb.AppendLine("- Return a complete, self-contained work report. The parent Agent must not repeat your work to understand the result.");
        sb.AppendLine("- Never answer only \"done\", \"completed\", \"success\", or another progress/status sentence.");
        sb.AppendLine("- Use all five canonical top-level sections exactly as `SUMMARY:`, `CHANGES:`, `EVIDENCE:`, `RISKS:`, and `BLOCKERS:` with no Markdown prefix.");
        sb.AppendLine("- Every section is mandatory. For a genuinely empty section, write `none` plus a short reason.");
        sb.AppendLine("- Claims must be tied to concrete files, symbols, sources, commands, test names, runtime observations, or produced artifacts.");
        sb.AppendLine();
    }

    protected async Task<ToolExecutionResult> RunSubAgentAsync(
        TArgs args,
        ToolExecutionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct,
        int? timeoutSeconds = null)
    {
        var task = BuildTaskPrompt(args, context);

        // Validate task has location context before spawning expensive sub-agent
        if (args is ScopedSmartWorkflowArgs { Scope: null or "" }
            && !TaskTextContainsLocation((args as SmartWorkflowArgs)?.Task ?? ""))
        {
            return ToolExecutionResult.Fail(
                "Task is missing location information (WHERE). " +
                "Please provide 'scope' (file path, directory, URL, or domain), " +
                "or include explicit paths in the task description. " +
                "Example: scope='Source/PuddingRuntime/Tools/' or task='In Source/Foo/Bar.cs, ...'");
        }

        var toolName = GetType().Name;
        var workingDirectory = ResolveWorkingDirectory(args);
        var requestedTimeout = timeoutSeconds ?? DefaultTimeoutSeconds;
        if (requestedTimeout <= 0)
            return ToolExecutionResult.Fail($"{toolName} timeout_seconds must be greater than 0.");

        // Smart 的角色预算是子任务上限，不是从调用时重新获得的独立父级预算。
        // 父 Run deadline 是唯一上界，并预留两分钟让父 Agent 消化工具结果和提交终态。
        var timeout = Math.Min(requestedTimeout, DefaultTimeoutSeconds);
        var parentFinalizationReserveSeconds =
            services.GetService<IRuntimeExecutionConfigService>()?
                .GetOptions()
                .SubAgents
                .ParentFinalizationReserveSeconds
            ?? DefaultParentFinalizationReserveSeconds;
        if (context.ExecutionDeadlineUtc is { } parentDeadlineUtc)
        {
            var remainingSeconds = (int)Math.Floor(
                (parentDeadlineUtc - DateTimeOffset.UtcNow).TotalSeconds
                - parentFinalizationReserveSeconds);
            if (remainingSeconds <= 0)
            {
                return ToolExecutionResult.Fail(JsonSerializer.Serialize(new
                {
                    errorCode = "insufficient_execution_budget",
                    tool = Descriptor.ToolId,
                    role = RoleName,
                    parentDeadlineUtc,
                    reserveSeconds = parentFinalizationReserveSeconds,
                    message = "The parent run has insufficient time remaining to start this Smart workflow.",
                }));
            }

            timeout = Math.Min(timeout, remainingSeconds);
        }

        // 从父 Agent manifest 解析角色对应的模型
        var configurationAgentId =
            context.ConfigurationAgentInstanceId ?? context.AgentInstanceId;
        var model = await ResolveRoleModelAsync(configurationAgentId, services, logger);

        var sw = Stopwatch.StartNew();

        try
        {
            var spawnArgs = JsonSerializer.Serialize(new
            {
                task,
                agent_template = SubAgentTemplateId,
                sync = true,
                model,
                role_in_plan = RoleName,
                timeout_seconds = timeout,
                max_rounds = DefaultMaxRounds,
                working_directory = workingDirectory,
                allow_sub_delegation = AllowNestedSmartDelegation,
                depth = context.DelegationDepth ?? 0,
                max_depth = context.MaxDelegationDepth ?? MaxDelegationDepth,
                tools = AllowedTools,
                // Smart task prompts are self-contained. Reusing either the
                // parent transcript or a pooled child session makes repeated
                // atomic calls accumulate unrelated context.
                reuse_parent_context = false,
                origin_tool_id = Descriptor.ToolId,
                output = CanonicalWorkReport.ExpectedOutputContract,
                expected_output_contract = CanonicalWorkReport.ExpectedOutputContract,
            });

            logger.LogInformation(
                "[{Tool}] agent={Agent} role={Role} spawning sub-agent timeout={Timeout}s",
                toolName, configurationAgentId, RoleName, timeout);

            var toolExec = services.GetRequiredService<IPuddingToolExecutionService>();
            var result = await toolExec.ExecuteAsync("spawn_sub_agent", spawnArgs, context, null, ct);

            // Model changes are externally visible execution decisions. Never
            // retry a different model unless the caller explicitly opted in.
            if (!result.Success && args is SmartWorkflowArgs { AllowFallback: true }
                && FallbackModelIds is { Count: > 0 } fallbacks
                && IsTransientSmartFailure(result.Error))
            {
                foreach (var fallbackModel in fallbacks)
                {
                    if (ct.IsCancellationRequested) break;
                    if (string.Equals(fallbackModel, model, StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    logger.LogWarning(
                        "[{Tool}] agent={Agent} role={Role} FALLBACK primary={Primary} -> fallback={Fallback}",
                        toolName, configurationAgentId, RoleName, model, fallbackModel);

                    var fallbackArgs = JsonSerializer.Serialize(new
                    {
                        task,
                        agent_template = SubAgentTemplateId,
                        sync = true,
                        model = fallbackModel,
                        role_in_plan = RoleName,
                        timeout_seconds = Math.Max(1, timeout / 2),
                        max_rounds = Math.Max(1, DefaultMaxRounds / 2),
                        working_directory = workingDirectory,
                        allow_sub_delegation = AllowNestedSmartDelegation,
                        depth = context.DelegationDepth ?? 0,
                        max_depth = context.MaxDelegationDepth ?? MaxDelegationDepth,
                        tools = AllowedTools,
                        reuse_parent_context = false,
                        origin_tool_id = Descriptor.ToolId,
                        output = CanonicalWorkReport.ExpectedOutputContract,
                        expected_output_contract = CanonicalWorkReport.ExpectedOutputContract,
                    });

                    result = await toolExec.ExecuteAsync("spawn_sub_agent", fallbackArgs, context, null, ct);
                    if (result.Success) break;
                }
            }

            sw.Stop();

            if (result.Success)
            {
                if (!TryValidateDetailedReport(result.Output, out var validationError, out var rawReport))
                {
                    logger.LogError(
                        "[{Tool}] agent={Agent} INVALID_REPORT reason={Reason} output={OutputLen} chars",
                        toolName, context.AgentInstanceId, validationError, rawReport?.Length ?? 0);
                    return BuildInvalidReportFailure(
                        result.Output,
                        rawReport,
                        validationError,
                        toolName);
                }

                logger.LogInformation(
                    "[{Tool}] agent={Agent} SUCCESS duration={Duration}ms output={OutputLen} chars",
                    toolName, context.AgentInstanceId, sw.ElapsedMilliseconds,
                    result.Output?.Length ?? 0);
                return ToolExecutionResult.Ok(result.Output!);
            }

            var failMsg = $"❌ {toolName} sub-agent FAILED.\n   Error: {result.Error}\n   Role: {RoleName}\n   Duration: {sw.ElapsedMilliseconds}ms";
            logger.LogError("[{Tool}] agent={Agent} FAILED error={Error}", toolName, context.AgentInstanceId, result.Error);
            return ToolExecutionResult.Fail(failMsg);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 父 Run 取消或 deadline 到达必须沿执行链传播，不能伪装成普通工具失败。
            throw;
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            var msg = $"{toolName} TIMED OUT after {sw.ElapsedMilliseconds}ms (limit={timeout}s, role={RoleName}).";
            logger.LogError("[{Tool}] TIMEOUT duration={Duration}ms", toolName, sw.ElapsedMilliseconds);
            return ToolExecutionResult.Fail(msg);
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "[{Tool}] EXCEPTION", toolName);
            return ToolExecutionResult.Fail($"{toolName} EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool IsTransientSmartFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return false;
        return error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("circuit breaker", StringComparison.OrdinalIgnoreCase)
            || error.Contains("task was canceled", StringComparison.OrdinalIgnoreCase)
            || error.Contains("stream_idle", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP 5", StringComparison.OrdinalIgnoreCase)
            || error.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Heuristic: check if task text contains explicit path or URL references.
    /// </summary>
    private static bool TaskTextContainsLocation(string task)
    {
        if (string.IsNullOrWhiteSpace(task))
            return false;
        
        // Check for filesystem paths
        if (task.Contains('/') || task.Contains('\\'))
            return true;
        
        // Check for URLs
        if (task.Contains("://") || task.Contains("www."))
            return true;
        
        // Common location indicators
        var locationKeywords = new[] { "Source/", "src/", "file:", "path:", "directory:", "module:", "package:" };
        foreach (var kw in locationKeywords)
            if (task.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        
        return false;
    }

    private ToolExecutionResult BuildInvalidReportFailure(
        string? toolOutput,
        string? rawReport,
        string validationError,
        string toolName)
    {
        string? subAgentId = null;
        string? runId = null;
        if (!string.IsNullOrWhiteSpace(toolOutput))
        {
            try
            {
                using var document = JsonDocument.Parse(toolOutput);
                var root = document.RootElement;
                subAgentId = TryGetString(root, "subAgentId");
                runId = TryGetString(root, "runId");
            }
            catch (JsonException)
            {
                // Alternative tool implementations may return the report directly.
            }
        }

        return new ToolExecutionResult
        {
            Success = false,
            // Preserve the complete spawn_sub_agent envelope, including rawOutput and stable IDs.
            Output = toolOutput ?? string.Empty,
            Error = JsonSerializer.Serialize(new
            {
                errorCode = "invalid_smart_workflow_report",
                tool = Descriptor.ToolId,
                implementation = toolName,
                role = RoleName,
                subAgentId,
                runId,
                message = $"{toolName} returned an incomplete work report.",
                validationError,
                rawOutputLength = rawReport?.Length ?? 0,
            }),
            ExitCode = 1,
        };
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryValidateDetailedReport(
        string? toolOutput,
        out string error,
        out string? rawReport)
    {
        rawReport = ExtractRawReport(toolOutput);
        return CanonicalWorkReport.TryValidate(rawReport, out error);
    }

    private static string? ExtractRawReport(string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            return null;

        try
        {
            using var document = JsonDocument.Parse(toolOutput);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // 优先读取 summary（BuildStructuredResult 产出的结构化字段）
                if (document.RootElement.TryGetProperty("summary", out var summary)
                    && summary.ValueKind == JsonValueKind.String)
                    return summary.GetString();
                // 向后兼容：rawOutput（一次性子代理的 BuildSingleToolOutput 仍产它）
                if (document.RootElement.TryGetProperty("rawOutput", out var rawOutput)
                    && rawOutput.ValueKind == JsonValueKind.String)
                    return rawOutput.GetString();
            }
        }
        catch (JsonException)
        {
            // Tests and alternative tool implementations may return the raw report directly.
        }

        return toolOutput;
    }

    private static string? ResolveWorkingDirectory(TArgs args)
    {
        if (args is not ScopedSmartWorkflowArgs { Scope: { } scope }
            || string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        try
        {
            if (Directory.Exists(scope))
                return Path.GetFullPath(scope);
            if (File.Exists(scope))
                return Path.GetDirectoryName(Path.GetFullPath(scope));
        }
        catch
        {
            // Scope can also be a semantic boundary. Only existing filesystem paths
            // become execution roots; all other scope text remains prompt-only.
        }

        return null;
    }

    /// <summary>
    /// Synthesize a WHERE (location) section from the scope parameter.
    /// Called by each Smart tool's BuildTaskPrompt to inject spatial context.
    /// </summary>
    protected static string BuildWhereSection(TArgs args)
    {
        if (args is not ScopedSmartWorkflowArgs scoped)
            return string.Empty;
        
        if (string.IsNullOrWhiteSpace(scoped.Scope))
            return string.Empty;
        
        var sb = new StringBuilder();
        sb.AppendLine("## 📍 LOCATION (WHERE)");
        sb.AppendLine($"Scope: {scoped.Scope}");
        
        try
        {
            if (Directory.Exists(scoped.Scope))
                sb.AppendLine($"Type: directory — {Path.GetFullPath(scoped.Scope)}");
            else if (File.Exists(scoped.Scope))
                sb.AppendLine($"Type: file — {Path.GetFullPath(scoped.Scope)}");
        }
        catch
        {
            // Scope may be a semantic boundary (URL, domain, concept), not a filesystem path
        }
        
        return sb.ToString();
    }

    /// <summary>
    /// 从父 Agent manifest 读取角色对应的模型。
    /// 用于 Smart 工具显式指定子代理模型，不依赖 spawn_sub_agent 内部逻辑。
    /// </summary>
    private async Task<string?> ResolveRoleModelAsync(string? agentInstanceId, IServiceProvider services, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(agentInstanceId))
            return null;

        try
        {
            var profileProvider = services.GetService<AgentProfileProvider>();
            if (profileProvider is null)
                return null;

            var profile = await profileProvider.LoadAsync(agentInstanceId, CancellationToken.None);
            var instance = profile.Instance;

            return RoleName.ToLowerInvariant() switch
            {
                "explorer" => instance.ExplorerModel,
                "researcher" => instance.ResearcherModel,
                "planner" => instance.PlannerModel,
                "reviewer" => instance.ReviewerModel,
                "developer" => instance.DeveloperModel,
                "deployer" => instance.DeployerModel,
                "tester" => instance.TesterModel,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[SmartWorkflow] Failed to resolve role model: role={Role} agent={Agent}",
                RoleName, agentInstanceId);
            return null;
        }
    }

    private static string BuildCompactSmartResult(string spawnSubAgentOutput)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(spawnSubAgentOutput);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return spawnSubAgentOutput;

            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String
                ? s.GetString()?.Trim() : null;
            var status = root.TryGetProperty("status", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.String
                ? st.GetString() : null;
            var subAgentId = root.TryGetProperty("subAgentId", out var aid) && aid.ValueKind == System.Text.Json.JsonValueKind.String
                ? aid.GetString() : null;
            var rawLen = 0;
            if (root.TryGetProperty("rawOutputLength", out var rol) && rol.ValueKind == System.Text.Json.JsonValueKind.Number)
                rawLen = rol.GetInt32();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"✅ 子代理完成: {subAgentId ?? "?"} | 状态: {status ?? "unknown"}");
            sb.AppendLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            if (!string.IsNullOrWhiteSpace(summary))
            {
                sb.AppendLine($"📋 SUMMARY: {summary}");
                sb.AppendLine();
            }

            if (root.TryGetProperty("changes", out var ch2) && ch2.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var changesList = new System.Collections.Generic.List<string>();
                foreach (var item in ch2.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        changesList.Add(item.GetString()!);
                if (changesList.Count > 0)
                {
                    sb.AppendLine($"🔧 CHANGES ({changesList.Count}):");
                    foreach (var c in changesList)
                        sb.AppendLine($"   - {c}");
                    sb.AppendLine();
                }
            }

            if (root.TryGetProperty("evidence", out var ev2) && ev2.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var evidenceList = new System.Collections.Generic.List<string>();
                foreach (var item in ev2.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        evidenceList.Add(item.GetString()!);
                if (evidenceList.Count > 0)
                {
                    var maxShow = Math.Min(evidenceList.Count, 5);
                    sb.AppendLine($"📎 EVIDENCE ({evidenceList.Count} 条, 显示前 {maxShow}):");
                    for (int i = 0; i < maxShow; i++)
                        sb.AppendLine($"   - {evidenceList[i]}");
                    if (evidenceList.Count > 5)
                        sb.AppendLine($"   ... 及其他 {evidenceList.Count - 5} 条");
                    sb.AppendLine();
                }
            }

            if (root.TryGetProperty("risks", out var r2) && r2.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var riskList = new System.Collections.Generic.List<string>();
                foreach (var item in r2.EnumerateArray())
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        riskList.Add(item.GetString()!);
                if (riskList.Count > 0)
                {
                    sb.AppendLine("⚠️ RISKS:");
                    foreach (var r in riskList)
                        sb.AppendLine($"   - {r}");
                    sb.AppendLine();
                }
            }

            if (rawLen > 0)
                sb.Append($"(子代理原始输出 {rawLen:N0} 字符已省略)");
            return sb.ToString().TrimEnd();
        }
        catch
        {
            throw;
        }
    }
}

/// <summary>
/// 所有 Smart 工作流工具共用的稳定请求合同。
/// 角色只决定执行策略，不改变主任务参数名称。
/// </summary>
public abstract class SmartWorkflowArgs
{
    [ToolParam("要交给对应角色子代理完成的任务")]
    public string? Task { get; set; }

    [ToolParam("子代理超时秒数；留空时使用角色默认值")]
    public int? TimeoutSeconds { get; set; }

    [ToolParam("主模型发生瞬态失败时是否允许显式切换到该 Smart 角色的备用模型；默认 false")]
    public bool AllowFallback { get; set; }
}

/// <summary>需要文件、会话或研究边界的 Smart 工作流请求。</summary>
public abstract class ScopedSmartWorkflowArgs : SmartWorkflowArgs
{
    [ToolParam("任务范围，例如目录、文件、会话或研究边界")]
    public string? Scope { get; set; }
}
