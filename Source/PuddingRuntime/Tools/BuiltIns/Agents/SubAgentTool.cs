using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Skills;

/// <summary>
/// SubAgentTool — 主 Agent 派生子代理执行任务的 Skill。
/// 
/// 设计原则：
///   · 复用 AgentExecutionService — 子代理与主代理使用同一执行引擎，不另起炉灶
///   · 权限继承 — 子代继承父代理的能力策略，父代理可下调（不可升级）
///   · 工具继承 — 默认继承父代理的工具集，可指定子集
///   · 模型路由 — 通过 ILlmResolver 从 llm.providers.json 唯一配置源解析身份与配置快照
///   · 同步模式 — 父代理等待子代理完成，结果注入父代理上下文
///   · 异步模式 — 父代理继续执行，子代理完成后通过事件系统回调通知
///   · 参数校验 — 无效模板名返回可用列表，不让 LLM 盲猜
///   · 延迟解析 — 使用 IServiceProvider 避免 AgentExecutionService 的 DI 死锁
/// 
/// 原生 Pudding Tool，对应 Claude Code AgentTool / SendMessageTool 的子代理模式。
/// </summary>
[Tool(
    id: "spawn_sub_agent",
    name: "spawn_sub_agent",
    description: "派生子代理执行独立任务。子代理拥有独立的上下文窗口，看不到主代理的对话历史。" +
                 "推荐使用结构化委派协议参数：question、scope、already_known、effort、stop_condition、output；" +
                 "也可以使用旧 task，或使用 tasks JSON array 批量发起多个结构化子任务。" +
                 "参数：task（任务描述）、agent_template（可选，默认 workspace-task-agent）、" +
                 "model（可选，如 mimo/mimo-v2.5-pro 或 deepseek/deepseek-v3，不指定则用平台默认模型）、" +
                 "sync（可选，true=同步阻塞等待结果 / false=异步立即返回，默认 true）。" +
                 "同步模式返回结构化结果合同：SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS。" +
                 "异步模式下立即返回 agentId，稍后通过 agent.sub_completed 事件通知结果。" +
                 "provider 格式为 {providerId}/{modelId}，平台已在 LLM 资源池注册模型。",
    category: ToolCategory.Orchestration,
    permission: ToolPermissionLevel.Low,
    safety: ToolSafetyFlags.None)]
public sealed class SubAgentTool : PuddingToolBase<SubAgentToolArgs>
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SubAgentTool> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string DelegationProtocolVersion = "SUBAGENTS.md/v1";
    private const string DefaultSubAgentOutputContract = "SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS";
    private const int DefaultSubAgentMaxRounds = 10;
    private const int MaximumSubAgentMaxRounds = 200;
    private static readonly string[] ResultSectionNames = ["SUMMARY", "CHANGES", "EVIDENCE", "RISKS", "BLOCKERS"];

    public SubAgentTool(
        IServiceProvider services,
        ILogger<SubAgentTool> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task<ToolExecutionResult> ExecuteCoreAsync(
        SubAgentToolArgs args,
        ToolExecutionContext context,
        CancellationToken ct)
    {
        var request = SubAgentToolRequest.From(args, context);
        var subAgentInvocation = _services.GetService<ISubAgentInvocationService>();
        if (subAgentInvocation is null)
            return ToolExecutionResult.Fail("Sub-agent service not registered");

        var json = TryParseJson(request.Input);
        if (json is null)
            return Fail("spawn_sub_agent requires a valid JSON object. Use either {\"task\":\"...\"} or {\"tasks\":[...]}.");

        var delegation = ReadDelegationProtocol(json, request);
        var task = GetStringProp(json, "task")
                ?? GetStringProp(json, "prompt")
                ?? request.Parameters.GetValueOrDefault("task");
        if (delegation.HasAnyField)
            task = RenderDelegationTask(task, delegation);
        var batchTasksResult = TryReadBatchTasks(json);

        if (!string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is not null)
            return Fail("参数 'task' 和 'tasks' 必须二选一，不能同时传入。");
        if (string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is null)
            return Fail("参数 'task' 或 'tasks' 是必需的。批量模式必须传入 JSON array。");
        if (batchTasksResult.Error is not null)
            return Fail(batchTasksResult.Error);

        var templateId = GetStringProp(json, "agent_template")
                      ?? GetStringProp(json, "template")
                      ?? request.Parameters.GetValueOrDefault("template")
                      ?? "workspace-task-agent";

        var isSync = GetBoolProp(json, "sync")
                  ?? (request.Parameters.TryGetValue("sync", out var syncVal)
                        && bool.TryParse(syncVal, out var syncBool) && syncBool);

        // 没有显式指定 sync → 默认同步
        if (!HasProp(json, "sync") && !request.Parameters.ContainsKey("sync"))
            isSync = true;

        var modelId = GetStringProp(json, "model")
                   ?? request.Parameters.GetValueOrDefault("model");
        var permissionMode = GetStringProp(json, "permission_mode")
                          ?? GetStringProp(json, "permissionMode")
                          ?? SubAgentPermissionModes.Inherit;
        var originToolId = GetStringProp(json, "origin_tool_id")
                        ?? GetStringProp(json, "originToolId")
                        ?? "spawn_sub_agent";
        var timeoutSeconds = GetIntArg(json, request, "timeout_seconds", "timeoutSeconds");
        var workingDirectory = GetStringArg(
            json,
            request,
            "working_directory",
            "workingDirectory");
        var maxRounds = GetIntArg(json, request, "max_rounds", "maxRounds")
                     ?? DefaultSubAgentMaxRounds;
        if (maxRounds is < 1 or > MaximumSubAgentMaxRounds)
        {
            return Fail(
                $"max_rounds must be between 1 and {MaximumSubAgentMaxRounds}. Received: {maxRounds}.");
        }
                var capabilityRequirements = GetStringProp(json, "capability_requirements")
                                 ?? GetStringProp(json, "capabilityRequirements")
                                 ?? request.Parameters.GetValueOrDefault("capability_requirements");

        // ── Session Fork: 复用父代理上下文 ──
        var reuseParentCtx = GetBoolProp(json, "reuse_parent_context")
            ?? GetBoolProp(json, "reuseParentContext")
            ?? args.ReuseParentContext;
        string? parentContextSnapshot = null;
        if (reuseParentCtx == true)
        {
            var ctxStore = _services.GetService<ContextAssemblyStore>();
            if (ctxStore?.TryGet(request.SessionId, out var snapshot) == true && snapshot is not null)
            {
                parentContextSnapshot = BuildParentContextSnapshot(snapshot);
                _logger.LogInformation(
                    "[SubAgent] SessionFork parentSession={Session} snapshotLayers={Layers}",
                    request.SessionId, snapshot.Layers.Count);
            }
        }

        // 确定子代理模板
        var template = ResolveTemplate(templateId);
        if (template == null)
        {
            var available = string.Join(", ", BuiltInAgentTemplates.GetAll().Select(t => t.TemplateId));
            return Fail($"未知的 Agent 模板 '{templateId}'。可用模板：{available}");
        }

        // 构造子代理的 Capability（继承父代理，可下调不可升级）
        var childCapability = BuildChildCapability(json, request, template, permissionMode);

        // 在调用入口一次性解析不可变路由身份和调用配置。
        // 后续 InvocationService / Manager 只能透传，禁止从 Endpoint、密钥或 model 字符串反推 Provider。
        ResolvedChildLlmRoute childLlmRoute;
        try
        {
            childLlmRoute = await ResolveChildLlmRouteAsync(
                modelId,
                capabilityRequirements,
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
        var taskPlanning = ReadTaskPlanningContext(json, request, task ?? batchTasksResult.Tasks![0].Task);
        var policyDeny = await CheckTaskPlanningPolicyAsync(taskPlanning, ct);
        if (policyDeny is not null)
            return policyDeny;
        var childDelegationDepth = (taskPlanning.DelegationDepth ?? 0) + 1;
        var childAllowSubDelegation = taskPlanning.AllowSubDelegation == true;

        _logger.LogInformation(
            "[SubAgent] Spawning sync={Sync} template={Template} provider={Provider} profile={Profile} model={Model} session={Session}",
            isSync,
            template.TemplateId,
            childLlmRoute.Profile.ProviderId,
            childLlmRoute.Profile.ProfileId,
            childLlmRoute.Profile.ModelId,
            request.SessionId);

        if (batchTasksResult.Tasks is not null)
        {
            try
            {
                var batch = await subAgentInvocation.InvokeBatchAsync(new SubAgentBatchInvocationRequest
                {
                    ParentSessionId = request.SessionId,
                    ParentAgentInstanceId = request.AgentInstanceId,
                    ParentAgentId = request.AgentInstanceId,
                    WorkspaceId = request.WorkspaceId,
                    WorkingDirectory = workingDirectory,
                    TemplateId = template.TemplateId,
                    Tasks = batchTasksResult.Tasks,
                    IsAsync = !isSync,
                    LlmConfig = childLlmRoute.Config,
                                    LlmProfile = childLlmRoute.Profile,
                ParentContextSnapshot = parentContextSnapshot,
                MaxRounds = maxRounds,
                CapabilityPolicy = childCapability,
                ParentTaskId = GetStringProp(json, "parent_task_id") ?? GetStringProp(json, "parentTaskId"),
                    TaskPlanId = taskPlanning.TaskPlanId,
                    ParentTaskNodeId = taskPlanning.ParentTaskNodeId,
                    DelegationDepth = childDelegationDepth,
                    MaxDelegationDepth = taskPlanning.MaxDelegationDepth,
                    RoleInPlan = taskPlanning.RoleInPlan,
                    AllowSubDelegation = childAllowSubDelegation,
                    AllowAgentCreation = taskPlanning.AllowAgentCreation,
                    PermissionMode = permissionMode,
                    TimeoutSeconds = timeoutSeconds,
                    BatchId = GetStringProp(json, "batch_id")
                           ?? GetStringProp(json, "batchId"),
                    OriginToolId = originToolId,
                    ParentExecutionIdentity = context.ExecutionIdentity,
                }, ct);

                return new ToolExecutionResult
                {
                    Success = batch.Status is "completed" or "running",
                    Output = BuildBatchToolOutput(batch),
                    Error = batch.Error,
                    ExitCode = batch.Status is "completed" or "running" ? 0 : 1,
                };
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message);
            }
        }

        try
        {
            var invocationResult = await subAgentInvocation.InvokeAsync(new SubAgentInvocationRequest
            {
                ParentSessionId = request.SessionId,
                ParentAgentInstanceId = request.AgentInstanceId,
                ParentAgentId = request.AgentInstanceId,
                WorkspaceId = request.WorkspaceId,
                WorkingDirectory = workingDirectory,
                TemplateId = template.TemplateId,
                Task = task!,
                DelegationProtocol = DelegationProtocolVersion,
                Question = delegation.Question,
                Scope = delegation.Scope,
                AlreadyKnown = delegation.AlreadyKnown,
                Effort = delegation.Effort,
                StopCondition = delegation.StopCondition,
                OutputContract = delegation.Output,
                IsAsync = !isSync,
                LlmConfig = childLlmRoute.Config,
                                LlmProfile = childLlmRoute.Profile,
                ParentContextSnapshot = parentContextSnapshot,
                MaxRounds = maxRounds,
                CapabilityPolicy = childCapability,
                TaskPlanId = taskPlanning.TaskPlanId,
                TaskNodeId = taskPlanning.TaskNodeId,
                ParentTaskNodeId = taskPlanning.ParentTaskNodeId,
                DelegationDepth = childDelegationDepth,
                MaxDelegationDepth = taskPlanning.MaxDelegationDepth,
                RoleInPlan = taskPlanning.RoleInPlan,
                AllowSubDelegation = childAllowSubDelegation,
                AllowAgentCreation = taskPlanning.AllowAgentCreation,
                AssignedObjective = taskPlanning.AssignedObjective,
                ExpectedOutputContract = taskPlanning.ExpectedOutputContract,
                PermissionMode = permissionMode,
                TimeoutSeconds = timeoutSeconds,
                InvocationId = GetStringProp(json, "invocation_id")
                            ?? GetStringProp(json, "invocationId"),
                OriginToolId = originToolId,
                ParentExecutionIdentity = context.ExecutionIdentity,
            }, ct);

            if (isSync)
            {
                return new ToolExecutionResult
                {
                    Success = invocationResult.Status == "completed",
                    Output = BuildSingleToolOutput(invocationResult),
                    Error = invocationResult.Status == "completed" ? null : invocationResult.Error,
                    ExitCode = invocationResult.Status == "completed" ? 0 : 1,
                };
            }

            _logger.LogInformation(
                "[SubAgent] Async spawned sub={SubAgent} parent={Parent}",
                invocationResult.SubSessionId, request.SessionId);

            return Success(
                $"异步子代理已创建。sub_agent_id = {invocationResult.SubSessionId}。" +
                $"完成后将通过 'agent.sub_completed' 事件通知。",
                new
                {
                    schema = "pudding-subagent-spawn",
                    version = 1,
                    sub_agent_id = invocationResult.SubSessionId,
                    task_id = invocationResult.TaskId,
                    async = true,
                    status = invocationResult.Status,
                    delegation_protocol = DelegationProtocolVersion,
                    output_contract = DefaultSubAgentOutputContract,
                });
        }
        catch (InvalidOperationException ex)
        {
            return Fail(ex.Message);
        }
    }

    // ── 私有辅助 ──────────────────────────────────────────────────────

    private static JsonObject? TryParseJson(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        try { return JsonNode.Parse(input)?.AsObject(); }
        catch { return null; }
    }

    private static string? GetStringProp(JsonObject? obj, string name)
    {
        if (obj == null) return null;
        return obj.TryGetPropertyValue(name, out var node) ? node?.GetValue<string>() : null;
    }

    private static bool? GetBoolProp(JsonObject? obj, string name)
    {
        if (obj == null) return null;
        if (!obj.TryGetPropertyValue(name, out var node) || node == null) return null;
        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
            return node.GetValue<bool>();
        if (bool.TryParse(node.GetValue<string>(), out var b))
            return b;
        return null;
    }

    private static bool HasProp(JsonObject? obj, string name)
    {
        if (obj == null) return false;
        return obj.TryGetPropertyValue(name, out _);
    }

    private static DelegationProtocolInput ReadDelegationProtocol(JsonObject? json, SubAgentToolRequest request)
    {
        var output = GetStringArg(json, request, "output", "output_contract", "outputContract")
                     ?? DefaultSubAgentOutputContract;

        return new DelegationProtocolInput(
            Question: GetStringArg(json, request, "question"),
            Scope: GetStringArg(json, request, "scope"),
            AlreadyKnown: GetStringArg(json, request, "already_known", "alreadyKnown"),
            Effort: NormalizeEffort(GetStringArg(json, request, "effort")),
            StopCondition: GetStringArg(json, request, "stop_condition", "stopCondition"),
            Output: output);
    }

    private static string RenderDelegationTask(string? legacyTask, DelegationProtocolInput input)
    {
        var lines = new List<string>
        {
            "Use the following structured sub-agent delegation protocol.",
            "",
            $"QUESTION: {input.Question ?? legacyTask ?? "(not specified)"}",
            $"SCOPE: {input.Scope ?? "(not specified)"}",
            $"ALREADY_KNOWN: {input.AlreadyKnown ?? "(none)"}",
            $"EFFORT: {input.Effort ?? "medium"}",
            $"STOP_CONDITION: {input.StopCondition ?? "Stop when the requested question can be answered with evidence, or when the stated scope is exhausted."}",
            $"OUTPUT: {input.Output}",
            "",
            "Return exactly these top-level sections, in this order:",
            "SUMMARY:",
            "CHANGES:",
            "EVIDENCE:",
            "RISKS:",
            "BLOCKERS:",
            "",
            "Evidence must use path:line references when source files are involved. If a section has no content, write \"none\"."
        };

        if (!string.IsNullOrWhiteSpace(legacyTask) && !string.Equals(legacyTask, input.Question, StringComparison.Ordinal))
        {
            lines.Add("");
            lines.Add("ADDITIONAL_TASK:");
            lines.Add(legacyTask);
        }

        return string.Join("\n", lines);
    }

    private static string? NormalizeEffort(string? effort)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return null;

        var value = effort.Trim().ToLowerInvariant();
        return value is "quick" or "medium" or "thorough"
            ? value
            : "medium";
    }

    private static BatchTaskParseResult TryReadBatchTasks(JsonObject? json)
    {
        if (json is null || !json.TryGetPropertyValue("tasks", out var node) || node is null)
            return new BatchTaskParseResult(null, null);

        if (node is not JsonArray array)
            return new BatchTaskParseResult(null, "Batch sub-agent invocation requires 'tasks' to be a JSON array.");

        var tasks = new List<SubAgentBatchTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject item)
                return new BatchTaskParseResult(null, $"tasks[{i}] must be a JSON object.");

            var taskId = GetStringProp(item, "task_id") ?? GetStringProp(item, "taskId");
            var itemDelegation = new DelegationProtocolInput(
                Question: GetStringProp(item, "question"),
                Scope: GetStringProp(item, "scope"),
                AlreadyKnown: GetStringProp(item, "already_known") ?? GetStringProp(item, "alreadyKnown"),
                Effort: NormalizeEffort(GetStringProp(item, "effort")),
                StopCondition: GetStringProp(item, "stop_condition") ?? GetStringProp(item, "stopCondition"),
                Output: GetStringProp(item, "output") ?? GetStringProp(item, "output_contract") ?? GetStringProp(item, "outputContract") ?? DefaultSubAgentOutputContract);
            var rawTaskText = GetStringProp(item, "task");
            var taskText = itemDelegation.HasAnyField
                ? RenderDelegationTask(rawTaskText, itemDelegation)
                : rawTaskText;
            var expectedOutput = GetStringProp(item, "expected_output") ?? GetStringProp(item, "expectedOutput");

            if (string.IsNullOrWhiteSpace(taskId))
                return new BatchTaskParseResult(null, $"tasks[{i}].task_id is required.");
            if (taskId.Length > 64 || !System.Text.RegularExpressions.Regex.IsMatch(taskId, "^[a-zA-Z0-9._:-]+$"))
                return new BatchTaskParseResult(null, $"tasks[{i}].task_id must use 1-64 chars from [a-zA-Z0-9._:-].");
            if (!seen.Add(taskId))
                return new BatchTaskParseResult(null, $"tasks[{i}].task_id is duplicated: {taskId}.");
            if (string.IsNullOrWhiteSpace(taskText))
                return new BatchTaskParseResult(null, $"tasks[{i}].task is required.");
            if (taskText.Length > 8000)
                return new BatchTaskParseResult(null, $"tasks[{i}].task is too long; maximum is 8000 chars.");
            if (expectedOutput?.Length > 2000)
                return new BatchTaskParseResult(null, $"tasks[{i}].expected_output is too long; maximum is 2000 chars.");

            tasks.Add(new SubAgentBatchTask
            {
                TaskId = taskId,
                Task = taskText,
                Question = itemDelegation.Question,
                Scope = itemDelegation.Scope,
                AlreadyKnown = itemDelegation.AlreadyKnown,
                Effort = itemDelegation.Effort,
                StopCondition = itemDelegation.StopCondition,
                OutputContract = itemDelegation.Output,
                ExpectedOutput = expectedOutput,
            });
        }

        return new BatchTaskParseResult(tasks, null);
    }

    private static string BuildSingleToolOutput(SubAgentInvocationResult result)
    {
        var structured = BuildStructuredResult(result);
        return JsonSerializer.Serialize(structured, JsonOpts);
    }

    private static string BuildBatchToolOutput(SubAgentBatchInvocationResult batch)
    {
        var output = new
        {
            schema = "pudding-subagent-batch-result",
            version = 1,
            batchId = batch.BatchId,
            status = batch.Status,
            summary = batch.Summary,
            error = batch.Error,
            delegationProtocol = DelegationProtocolVersion,
            outputContract = DefaultSubAgentOutputContract,
            results = batch.Results.Select(BuildStructuredResult).ToArray(),
        };

        return JsonSerializer.Serialize(output, JsonOpts);
    }

    private static object BuildStructuredResult(SubAgentInvocationResult result)
    {
        var sections = ExtractResultSections(result.Reply);
        return new
        {
            schema = "pudding-subagent-result",
            version = 1,
            subAgentId = result.SubSessionId,
            taskId = result.TaskId,
            status = result.Status,
            summary = GetSectionOrFallback(sections, "SUMMARY", result.Reply),
            changes = SplitSectionList(sections.GetValueOrDefault("CHANGES")),
            evidence = SplitSectionList(sections.GetValueOrDefault("EVIDENCE")),
            risks = SplitSectionList(sections.GetValueOrDefault("RISKS")),
            blockers = SplitSectionList(sections.GetValueOrDefault("BLOCKERS")),
            error = result.Error,
            rawOutput = result.Reply,
        };
    }

    private static Dictionary<string, string> ExtractResultSections(string? text)
    {
        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return sections;

        string? current = null;
        var builder = new System.Text.StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = line.Trim();
            var matched = ResultSectionNames.FirstOrDefault(name =>
                trimmed.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));

            if (matched is not null)
            {
                if (current is not null)
                    sections[current] = builder.ToString().Trim();

                current = matched;
                builder.Clear();
                var inline = trimmed[(matched.Length + 1)..].Trim();
                if (inline.Length > 0)
                    builder.AppendLine(inline);
                continue;
            }

            if (current is not null)
                builder.AppendLine(line);
        }

        if (current is not null)
            sections[current] = builder.ToString().Trim();

        return sections;
    }

    private static string? GetSectionOrFallback(
        IReadOnlyDictionary<string, string> sections,
        string sectionName,
        string? raw)
    {
        if (sections.TryGetValue(sectionName, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        return raw?
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> SplitSectionList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var lines = value
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', ' ', '\t'))
            .Where(line => !string.Equals(line, "none", StringComparison.OrdinalIgnoreCase))
            .Where(line => line.Length > 0)
            .ToArray();

        return lines;
    }

    private async Task<ToolExecutionResult?> CheckTaskPlanningPolicyAsync(
        TaskPlanningSpawnContext planning,
        CancellationToken ct)
    {
        if (!planning.HasTaskContext)
            return null;

        var taskStore = _services.GetService<ITaskPlanStore>();
        var policy = _services.GetService<ITaskDelegationPolicy>();
        if (taskStore is null || policy is null)
        {
            return Fail("任务规划委派策略未注册，无法创建带 task planning 上下文的子代理。");
        }

        var plan = await taskStore.GetPlanAsync(planning.TaskPlanId!, ct);
        if (plan is null)
            return Fail($"任务规划计划不存在：{planning.TaskPlanId}");

        var node = await taskStore.GetNodeAsync(planning.TaskNodeId!, ct);
        if (node is null)
            return Fail($"任务节点不存在：{planning.TaskNodeId}");

        if (!string.Equals(node.PlanId, plan.PlanId, StringComparison.Ordinal))
            return Fail($"任务节点 {node.TaskNodeId} 不属于计划 {plan.PlanId}。");

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.SubAgent, ct);
        if (decision.Allowed)
            return null;

        return Fail(
            $"任务规划策略拒绝创建子代理：{decision.Reason} " +
            $"(depth={decision.CurrentDepth}, max_depth={decision.MaxDepth})");
    }

    private static TaskPlanningSpawnContext ReadTaskPlanningContext(
        JsonObject? json,
        SubAgentToolRequest request,
        string task)
    {
        var planId = GetStringArg(json, request, "plan_id", "task_plan_id", "taskPlanId", "TaskPlanId");
        var taskNodeId = GetStringArg(json, request, "task_node_id", "taskNodeId", "TaskNodeId");

        return new TaskPlanningSpawnContext(
            TaskPlanId: planId,
            TaskNodeId: taskNodeId,
            ParentTaskNodeId: GetStringArg(json, request, "parent_task_node_id", "parentTaskNodeId", "ParentTaskNodeId"),
            DelegationDepth: GetIntArg(json, request, "depth", "delegation_depth", "delegationDepth", "DelegationDepth"),
            MaxDelegationDepth: GetIntArg(json, request, "max_depth", "max_delegation_depth", "maxDelegationDepth", "MaxDelegationDepth"),
            RoleInPlan: GetStringArg(json, request, "role_in_plan", "roleInPlan", "RoleInPlan"),
            AllowSubDelegation: GetBoolArg(json, request, "allow_sub_delegation", "allowSubDelegation", "AllowSubDelegation"),
            AllowAgentCreation: GetBoolArg(json, request, "allow_agent_creation", "allowAgentCreation", "AllowAgentCreation"),
            AssignedObjective: GetStringArg(json, request, "assigned_objective", "assignedObjective", "AssignedObjective") ?? task,
            ExpectedOutputContract: GetStringArg(json, request, "expected_output_contract", "expectedOutputContract", "ExpectedOutputContract"));
    }

    private static string? GetStringArg(JsonObject? json, SubAgentToolRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var fromJson = GetStringProp(json, name);
            if (!string.IsNullOrWhiteSpace(fromJson))
                return fromJson;

            if (request.Parameters.TryGetValue(name, out var fromParam) && !string.IsNullOrWhiteSpace(fromParam))
                return fromParam;
        }

        return null;
    }

    private static int? GetIntArg(JsonObject? json, SubAgentToolRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            if (json is not null && json.TryGetPropertyValue(name, out var node) && node is not null)
            {
                if (int.TryParse(node.ToString(), out var parsedJsonInt))
                    return parsedJsonInt;
            }

            if (request.Parameters.TryGetValue(name, out var fromParam) && int.TryParse(fromParam, out var parsedParamInt))
                return parsedParamInt;
        }

        return null;
    }

    private static bool? GetBoolArg(JsonObject? json, SubAgentToolRequest request, params string[] names)
    {
        foreach (var name in names)
        {
            var fromJson = GetBoolProp(json, name);
            if (fromJson.HasValue)
                return fromJson.Value;

            if (request.Parameters.TryGetValue(name, out var fromParam) && bool.TryParse(fromParam, out var parsedParamBool))
                return parsedParamBool;
        }

        return null;
    }

    /// <summary>解析模板 ID，支持精确匹配 + 模糊回退。</summary>
    private static AgentTemplateDefinition? ResolveTemplate(string templateId)
    {
        return BuiltInAgentTemplates.ResolveBest(templateId);
    }

    /// <summary>
    /// 子代理能力：继承父代理策略，可下调不可升级。
    /// 父代理可通过参数指定 AllowedToolNames 子集。
    /// </summary>
    private CapabilityPolicy BuildChildCapability(
        JsonObject? json,
        SubAgentToolRequest request,
        AgentTemplateDefinition template,
        string permissionMode)
    {
        var basePolicy = template.Capability ?? new CapabilityPolicy();

        // 允许调用的工具子集
        var toolsJson = GetStringProp(json, "tools");
        var toolsParam = request.Parameters.GetValueOrDefault("tools");
        var toolsStr = toolsJson ?? toolsParam;

        if (!string.IsNullOrWhiteSpace(toolsStr))
        {
            var allowedTools = toolsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            basePolicy = basePolicy with { AllowedToolNames = allowedTools };
        }

        if (!string.Equals(permissionMode, SubAgentPermissionModes.Low, StringComparison.OrdinalIgnoreCase))
            return basePolicy;

        var registry = _services.GetService<IPuddingToolRegistry>();
        if (registry is null)
            return basePolicy with
            {
                AllowShellExecution = false,
                AllowFileWrite = false,
                AllowNetworkAccess = false,
                RequiresGrantToolNames = [],
            };

        var lowToolIds = registry.ListDescriptors()
            .Where(d => d.PermissionLevel == ToolPermissionLevel.Low)
            .Select(d => d.ToolId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var explicitTools = basePolicy.GetAllEffectiveToolNames();
        var allowed = explicitTools.Count == 0
            ? lowToolIds
            : explicitTools.Where(lowToolIds.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (basePolicy.AllowedToolNames.Count > 0)
            allowed.IntersectWith(basePolicy.AllowedToolNames);

        return basePolicy with
        {
            AllowShellExecution = false,
            AllowFileWrite = false,
            AllowNetworkAccess = false,
            AllowedToolNames = allowed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            DefaultToolNames = allowed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray(),
            RequiresGrantToolNames = [],
        };
    }

    /// <summary>
    /// 从统一 LLM Resolver 获取唯一配置源已经解析好的 Provider/Model 与配置快照，
    /// 本层只补充子代理调用语义（ProfileId/Role）。
    /// </summary>
    private async Task<ResolvedChildLlmRoute> ResolveChildLlmRouteAsync(
        string? modelId,
        string? capabilityRequirements,
        CancellationToken ct)
    {
        var resolver = _services.GetRequiredService<ILlmResolver>();
        var requiredTags = capabilityRequirements?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tag => tag.Length > 0)
            .ToArray();
        var resolved = await resolver.ResolveRouteAsync(modelId, requiredTags, ct);

        var profile = new LlmInvocationProfile
        {
            ProviderId = resolved.ProviderId,
            ProfileId = "subagent.conscious",
            ModelId = resolved.ModelId,
            Role = "conscious",
        };
        return new ResolvedChildLlmRoute(profile, resolved.Config);
    }

    private static string TruncateForLog(string text, int maxLen)
        => text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";

    private static ToolExecutionResult Success(string message, object? detail = null)
    {
        var output = detail is not null
            ? JsonSerializer.Serialize(new { summary = message, detail }, JsonOpts)
            : message;
        return ToolExecutionResult.Ok(output);
    }

    private static ToolExecutionResult Fail(string error) => ToolExecutionResult.Fail(error);

    private sealed record ResolvedChildLlmRoute(
        LlmInvocationProfile Profile,
        LlmConfig Config);

    private sealed record SubAgentToolRequest(
        string Input,
        IReadOnlyDictionary<string, string> Parameters,
        string WorkspaceId,
        string SessionId,
        string AgentInstanceId)
    {
        public static SubAgentToolRequest From(SubAgentToolArgs args, ToolExecutionContext context)
        {
            var input = BuildInputJson(args);
            return new(
                input,
                ExtractParametersFromJson(input),
                context.WorkspaceId,
                context.SessionId,
                context.AgentInstanceId);
        }

        private static string BuildInputJson(SubAgentToolArgs args)
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["task"] = args.Task,
                ["tasks"] = args.Tasks?.Select(t => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["task_id"] = t.TaskId,
                    ["task"] = t.Task,
                    ["question"] = t.Question,
                    ["scope"] = t.Scope,
                    ["already_known"] = t.AlreadyKnown,
                    ["effort"] = t.Effort,
                    ["stop_condition"] = t.StopCondition,
                    ["output"] = t.Output,
                    ["expected_output"] = t.ExpectedOutput,
                }).ToArray(),
                ["question"] = args.Question,
                ["scope"] = args.Scope,
                ["already_known"] = args.AlreadyKnown,
                ["effort"] = args.Effort,
                ["stop_condition"] = args.StopCondition,
                ["output"] = args.Output,
                ["agent_template"] = args.AgentTemplate,
                ["template"] = args.Template,
                ["sync"] = args.Sync,
                ["model"] = args.Model,
                ["tools"] = args.Tools,
                ["permission_mode"] = args.PermissionMode,
                ["timeout_seconds"] = args.TimeoutSeconds,
                ["max_rounds"] = args.MaxRounds,
                ["working_directory"] = args.WorkingDirectory,
                ["parent_task_id"] = args.ParentTaskId,
                ["plan_id"] = args.PlanId,
                ["task_plan_id"] = args.TaskPlanId,
                ["task_node_id"] = args.TaskNodeId,
                ["parent_task_node_id"] = args.ParentTaskNodeId,
                ["depth"] = args.Depth,
                ["max_depth"] = args.MaxDepth,
                ["role_in_plan"] = args.RoleInPlan,
                ["allow_sub_delegation"] = args.AllowSubDelegation,
                ["allow_agent_creation"] = args.AllowAgentCreation,
                ["assigned_objective"] = args.AssignedObjective,
                ["expected_output_contract"] = args.ExpectedOutputContract,
            };

            return JsonSerializer.Serialize(values, JsonOpts);
        }

        private static IReadOnlyDictionary<string, string> ExtractParametersFromJson(string? argumentsJson)
        {
            if (string.IsNullOrWhiteSpace(argumentsJson))
                return new Dictionary<string, string>();

            try
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return new Dictionary<string, string>();

                return doc.RootElement.EnumerateObject()
                    .Select(p => (p.Name, Value: ConvertJsonValueToParameterString(p.Value)))
                    .Where(p => p.Value is not null)
                    .ToDictionary(
                        p => p.Name,
                        p => p.Value!,
                        StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>();
            }
        }

        private static string? ConvertJsonValueToParameterString(JsonElement value)
            => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null,
            };
    }

    private sealed record TaskPlanningSpawnContext(
        string? TaskPlanId,
        string? TaskNodeId,
        string? ParentTaskNodeId,
        int? DelegationDepth,
        int? MaxDelegationDepth,
        string? RoleInPlan,
        bool? AllowSubDelegation,
        bool? AllowAgentCreation,
        string? AssignedObjective,
        string? ExpectedOutputContract)
    {
        public bool HasTaskContext =>
            !string.IsNullOrWhiteSpace(TaskPlanId) &&
            !string.IsNullOrWhiteSpace(TaskNodeId);
    }

    private sealed record DelegationProtocolInput(
        string? Question,
        string? Scope,
        string? AlreadyKnown,
        string? Effort,
        string? StopCondition,
        string Output)
    {
        public bool HasAnyField =>
            !string.IsNullOrWhiteSpace(Question)
            || !string.IsNullOrWhiteSpace(Scope)
            || !string.IsNullOrWhiteSpace(AlreadyKnown)
            || !string.IsNullOrWhiteSpace(Effort)
            || !string.IsNullOrWhiteSpace(StopCondition);
    }

        private sealed record BatchTaskParseResult(IReadOnlyList<SubAgentBatchTask>? Tasks, string? Error);

    /// <summary>从父代理上下文快照构建子代理继承的上下文字符串。</summary>
    /// <remarks>
    /// v2: 静态层（L0-L2）输出 FullContent 原文（零剪枝，保证 KV-cache 前缀一致）；
    /// 动态层仅输出摘要元数据。
    /// </remarks>
    private static string BuildParentContextSnapshot(ContextAssemblySnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: INHERITED-CONTEXT ---");
        sb.AppendLine("[以下上下文从父代理会话 Fork，已剪枝：移除工具调用、思维链、心跳]");
        sb.AppendLine($"父会话: {snapshot.SessionId}");
        sb.AppendLine($"组装时间: {snapshot.AssembledAt:O}");
        sb.AppendLine($"总 Token 数: {snapshot.TotalTokens}");
        sb.AppendLine($"静态层指纹(SHA-256): {snapshot.StaticLayersFingerprint ?? "无"}");
        if (!string.IsNullOrEmpty(snapshot.StaticLayersFingerprint))
            sb.AppendLine("子代理可对比自身静态层指纹确认 KV-cache 是否可命中。");
        sb.AppendLine();

        // 静态层：原样输出 FullContent
        var staticLayers = snapshot.Layers
            .Where(l => l.IsStatic && !string.IsNullOrWhiteSpace(l.FullContent))
            .ToList();
        if (staticLayers.Count > 0)
        {
            sb.AppendLine("## 继承静态层（逐字节一致，保证 KV-cache 命中）");
            sb.AppendLine();
            foreach (var layer in staticLayers)
            {
                sb.AppendLine($"--- LAYER: {layer.LayerName} ---");
                sb.AppendLine(layer.FullContent);
                sb.AppendLine();
            }
        }

        // 动态层：仅输出摘要
        var dynamicLayers = snapshot.Layers
            .Where(l => !l.IsStatic && !string.IsNullOrWhiteSpace(l.ContentPreview))
            .ToList();
        if (dynamicLayers.Count > 0)
        {
            sb.AppendLine("## 父代理动态层摘要");
            foreach (var layer in dynamicLayers)
            {
                sb.AppendLine($"- [{layer.LayerName}] ({layer.TokenCount} tokens): {TruncatePreview(layer.ContentPreview, 500)}");
            }
        }

        // P1: 父代理最近 N 轮对话（两级传递：最近消息全文 + 更早摘要）
        if (snapshot.RecentMessages is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## 父代理对话历史（剪枝后）");
            var recentCount = Math.Min(snapshot.RecentMessages.Count, 6);
            for (int i = 0; i < recentCount; i++)
            {
                var msg = snapshot.RecentMessages[i];
                sb.AppendLine($"[{msg.Role}]: {TruncatePreview(msg.Content, 1000)}");
            }
            if (snapshot.RecentMessages.Count > 6)
            {
                sb.AppendLine($"... (共 {snapshot.RecentMessages.Count} 条剪枝消息，以上为最近 {recentCount} 条)");
            }
        }
        return sb.ToString();
    }

    private static string TruncatePreview(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLen)
            return text ?? string.Empty;
        return text[..maxLen] + "...";
    }
}

public sealed record SubAgentToolArgs
{
    [ToolParam("Legacy free-form task description. Prefer question/scope/already_known/effort/stop_condition/output for structured delegation.")]
    public string? Task { get; init; }

    [ToolParam("Optional JSON array of structured sub-agent tasks. Each item requires task_id and either task or question. Do not combine with task.")]
    public IReadOnlyList<SubAgentToolTaskArgs>? Tasks { get; init; }

    [ToolParam("Structured delegation QUESTION: one clear question for the sub-agent to answer.")]
    public string? Question { get; init; }

    [ToolParam("Structured delegation SCOPE: files, directories, PR, session, or other review boundary.")]
    public string? Scope { get; init; }

    [ToolParam("Structured delegation ALREADY_KNOWN: facts already known; prevents repeated work.")]
    public string? AlreadyKnown { get; init; }

    [ToolParam("Structured delegation EFFORT: quick, medium, or thorough.")]
    public string? Effort { get; init; }

    [ToolParam("Structured delegation STOP_CONDITION: when the sub-agent should stop.")]
    public string? StopCondition { get; init; }

    [ToolParam("Structured delegation OUTPUT fields. Default: SUMMARY, CHANGES, EVIDENCE, RISKS, BLOCKERS.")]
    public string? Output { get; init; }

    [ToolParam("Optional agent template id.")]
    public string? AgentTemplate { get; init; }

    [ToolParam("Optional template alias for agent_template.")]
    public string? Template { get; init; }

    [ToolParam("true to wait for completion, false to run asynchronously.")]
    public bool? Sync { get; init; }

        [ToolParam("Optional model id or provider/model id.")]
    public string? Model { get; init; }

    [ToolParam("复用父代理已组装的上下文（Fork + 剪枝后注入子代理）。默认 false。")]
    public bool? ReuseParentContext { get; init; }

    [ToolParam("Optional comma-separated allowed tool id subset for the child agent.")]
    public string? Tools { get; init; }

    [ToolParam("Permission inheritance mode: inherit or low.")]
    public string? PermissionMode { get; init; }

    [ToolParam("Optional sub-agent timeout. Must not exceed runtime.execution.json maxTimeoutSeconds.")]
    public int? TimeoutSeconds { get; init; }

    [ToolParam("Maximum child Agent Loop rounds, 1-200. Default: 10.")]
    public int? MaxRounds { get; init; }

    [ToolParam("Optional child file-tool root directory. WorkspaceId remains a business identity and is not converted to a path.")]
    public string? WorkingDirectory { get; init; }

    [ToolParam("Optional parent task id.")]
    public string? ParentTaskId { get; init; }

    [ToolParam("Optional task planning plan id.")]
    public string? PlanId { get; init; }

    [ToolParam("Optional task planning plan id alias.")]
    public string? TaskPlanId { get; init; }

    [ToolParam("Optional current task node id.")]
    public string? TaskNodeId { get; init; }

    [ToolParam("Optional parent task node id.")]
    public string? ParentTaskNodeId { get; init; }

    [ToolParam("Optional current delegation depth.")]
    public int? Depth { get; init; }

    [ToolParam("Optional maximum delegation depth.")]
    public int? MaxDepth { get; init; }

    [ToolParam("Optional task planning role.")]
    public string? RoleInPlan { get; init; }

    [ToolParam("Whether the child agent may create further sub-agents.")]
    public bool? AllowSubDelegation { get; init; }

    [ToolParam("Whether the child agent may create agents.")]
    public bool? AllowAgentCreation { get; init; }

    [ToolParam("Assigned objective for task planning delegation.")]
    public string? AssignedObjective { get; init; }

    [ToolParam("Expected output contract for task planning delegation.")]
    public string? ExpectedOutputContract { get; init; }
}

public sealed record SubAgentToolTaskArgs
{
    [ToolParam("Stable task id for this batch item.")]
    public string? TaskId { get; init; }

    [ToolParam("Free-form task text for this batch item.")]
    public string? Task { get; init; }

    [ToolParam("Structured delegation question for this batch item.")]
    public string? Question { get; init; }

    [ToolParam("Structured delegation scope for this batch item.")]
    public string? Scope { get; init; }

    [ToolParam("Known facts for this batch item.")]
    public string? AlreadyKnown { get; init; }

    [ToolParam("Effort hint: quick, medium, or thorough.")]
    public string? Effort { get; init; }

    [ToolParam("Stop condition for this batch item.")]
    public string? StopCondition { get; init; }

    [ToolParam("Output contract for this batch item.")]
    public string? Output { get; init; }

    [ToolParam("Expected output description for this batch item.")]
    public string? ExpectedOutput { get; init; }
}
