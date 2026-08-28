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
/// SubAgentTool �� �� Agent �����Ӵ���ִ������� Skill��
/// 
/// ���ԭ��
///   �� ���� AgentExecutionService �� �Ӵ�����������ʹ��ͬһִ�����棬������¯��
///   �� Ȩ�޼̳� �� �Ӵ��̳и��������������ԣ����������µ�������������
///   �� ���߼̳� �� Ĭ�ϼ̳и������Ĺ��߼�����ָ���Ӽ�
///   �� ģ��·�� �� ͨ�� ILlmResolver �� llm.providers.json Ψһ����Դ�������������ÿ���
///   �� ͬ��ģʽ �� �������ȴ��Ӵ�����ɣ����ע�븸����������
///   �� �첽ģʽ �� ����������ִ�У��Ӵ�����ɺ�ͨ���¼�ϵͳ�ص�֪ͨ
///   �� ����У�� �� ��Чģ�������ؿ����б������� LLM ä��
///   �� �ӳٽ��� �� ʹ�� IServiceProvider ���� AgentExecutionService �� DI ����
/// 
/// ԭ�� Pudding Tool����Ӧ Claude Code AgentTool / SendMessageTool ���Ӵ���ģʽ��
/// </summary>
[Tool(
    id: "spawn_sub_agent",
    name: "spawn_sub_agent",
    description: "派生子代理执行独立任务。子代理拥有独立的上下文窗口，看不到主代理的对话历史。" +
                 "推荐使用结构化委派协议参数：question、scope、already_known、effort、stop_condition、output；" +
                 "也可以使用旧 task，或使用 tasks JSON array 批量发起多个结构化子任务。" +
                 "参数：task（任务描述）、agent_template（可选，默认 workspace-task-agent）、" +
                 "model（可选，必须是 providerId/modelId 完整路由，如 deepseek/deepseek-v3；" +
                 "多 provider 注册的裸 modelId 会报错，先用 list_llm_providers 查 route，不指定则用平台默认模型）、" +
                 "fallback_model（可选，模型路由解析失败时自动回退一次并在结果中告警，默认 bigmodel/glm-5.3-flash；与 model 相同则禁用回退）、" +
                 "sync（可选，true=同步阻塞等待结果 / false=异步立即返回，默认 true）。" +
                 "同步模式返回结构化结果合同：SUMMARY、CHANGES、EVIDENCE、RISKS、BLOCKERS。" +
                 "异步模式下立即返回 agentId，稍后通过 subagent_result 消息通道通知结果。" +
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
    private static readonly string[] ResultSectionNames = ["SUMMARY", "CHANGES", "EVIDENCE", "RISKS", "BLOCKERS"];
    private const string DefaultFallbackModelRoute = "bigmodel/glm-5.3-flash";

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
        // 模型路由回退告警一次性穿透所有返回路径（sync/async/pool/batch 共用）。
        var routeAdvisories = new List<string>();
        var result = await ExecuteCoreInternalAsync(args, context, ct, routeAdvisories);
        if (routeAdvisories.Count > 0 && result.Success && !string.IsNullOrEmpty(result.Output))
            result = result with { Output = string.Join("\n", routeAdvisories) + "\n\n" + result.Output };
        return result;
    }

    private async Task<ToolExecutionResult> ExecuteCoreInternalAsync(
        SubAgentToolArgs args,
        ToolExecutionContext context,
        CancellationToken ct,
        List<string> routeAdvisories)
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
        var resumeSubAgentId = GetStringProp(json, "resume_sub_agent_id")
                            ?? GetStringProp(json, "resumeSubAgentId")
                            ?? args.ResumeSubAgentId;

        var hasBatchTasksArgument = json.TryGetPropertyValue("tasks", out var tasksNode)
                                    && tasksNode is not null;
        if (!string.IsNullOrWhiteSpace(resumeSubAgentId) && hasBatchTasksArgument)
            return Fail("resume_sub_agent_id can only be used with a single task, not tasks batch mode.");
        if (!string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is not null)
            return Fail("���� 'task' �� 'tasks' �����ѡһ������ͬʱ���롣");
        if (string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is null)
            return Fail("���� 'task' �� 'tasks' �Ǳ���ġ�����ģʽ���봫�� JSON array��");
        if (batchTasksResult.Error is not null)
            return Fail(batchTasksResult.Error);
        if (!string.IsNullOrWhiteSpace(resumeSubAgentId) && !string.IsNullOrWhiteSpace(args.PoolName))
            return Fail("resume_sub_agent_id cannot be combined with pool_name; pooled agents already own a reusable session identity.");

        var templateId = GetStringProp(json, "agent_template")
                      ?? GetStringProp(json, "template")
                      ?? request.Parameters.GetValueOrDefault("template")
                      ?? "workspace-task-agent";

        var isSync = GetBoolProp(json, "sync")
                  ?? (request.Parameters.TryGetValue("sync", out var syncVal)
                        && bool.TryParse(syncVal, out var syncBool) && syncBool);

        // û����ʽָ�� sync �� Ĭ��ͬ��
        if (!HasProp(json, "sync") && !request.Parameters.ContainsKey("sync"))
            isSync = true;

                var modelId = GetStringProp(json, "model")
                   ?? request.Parameters.GetValueOrDefault("model");
        var fallbackModel = GetStringProp(json, "fallback_model")
                        ?? GetStringProp(json, "fallbackModel")
                        ?? request.Parameters.GetValueOrDefault("fallback_model");
        var permissionMode = GetStringProp(json, "permission_mode")
                          ?? GetStringProp(json, "permissionMode")
                          ?? SubAgentPermissionModes.Inherit;
        var originToolId = GetStringProp(json, "origin_tool_id")
                        ?? GetStringProp(json, "originToolId")
                        ?? "spawn_sub_agent";
        var workingDirectory = GetStringArg(
            json,
            request,
            "working_directory",
            "workingDirectory");
                var capabilityRequirements = GetStringProp(json, "capability_requirements")
                                 ?? GetStringProp(json, "capabilityRequirements")
                                 ?? request.Parameters.GetValueOrDefault("capability_requirements");

        // ���� Session Fork: ���ø����������� ����
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

        // ȷ���Ӵ���ģ��
        var template = ResolveTemplate(templateId);
        if (template == null)
        {
            var available = string.Join(", ", BuiltInAgentTemplates.GetAll().Select(t => t.TemplateId));
            return Fail($"δ֪�� Agent ģ�� '{templateId}'������ģ�壺{available}");
        }

        // �����Ӵ����� Capability���̳и����������µ�����������
        var childCapability = BuildChildCapability(json, request, template, permissionMode);

        // �ڵ������һ���Խ������ɱ�·�����ݺ͵������á�
        // ���� InvocationService / Manager ֻ��͸������ֹ�� Endpoint����Կ�� model �ַ������� Provider��
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
            // 仅对「路由解析失败」自动回退一次；运行中 LLM 调用失败不在此回退。
            var fallbackRoute = string.IsNullOrWhiteSpace(fallbackModel)
                ? DefaultFallbackModelRoute
                : fallbackModel.Trim();
            if (string.Equals(fallbackRoute, modelId?.Trim(), StringComparison.OrdinalIgnoreCase))
                return Fail(ex.Message);

            try
            {
                childLlmRoute = await ResolveChildLlmRouteAsync(
                    fallbackRoute,
                    capabilityRequirements,
                    ct);
            }
            catch (InvalidOperationException fallbackEx)
            {
                return Fail(
                    $"{ex.Message} | 自动回退路由 '{fallbackRoute}' 同样解析失败：{fallbackEx.Message}");
            }

            routeAdvisories.Add(
                $"⚠️ [spawn_sub_agent] 模型路由回退告警：请求的模型路由 " +
                $"'{(string.IsNullOrWhiteSpace(modelId) ? "(未指定，平台默认)" : modelId)}' 解析失败（{ex.Message}），" +
                $"已自动回退到 '{fallbackRoute}' 执行本次任务。后续派发请用 list_llm_providers 查询并显式指定可用路由。");
            _logger.LogWarning(
                "[SubAgent] Child LLM route fallback original={OriginalRoute} fallback={FallbackRoute} reason={Reason}",
                modelId,
                fallbackRoute,
                ex.Message);
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

        // === �ػ��Ӵ���·�� ===
        // �� pool_name �ǿ�ʱ���߳ػ�����·����������ԭ��һ�����Ӵ����߼���
        if (!string.IsNullOrWhiteSpace(args.PoolName))
        {
            var pool = _services.GetService<ISubAgentPool>();
            if (pool == null)
                return ToolExecutionResult.Fail(
                    "? SubAgentPool ����δע�ᡣ���� DI ���á�");

            // ����ģʽ��֧�ֳػ��������ͻ��
            if (batchTasksResult.Tasks is not null)
                return Fail("��������ģʽ (tasks) ��֧�ֳػ��Ӵ�������ʹ�õ����� (task) + pool_name��");

            var action = args.PoolAction?.ToLowerInvariant() ?? "execute";

            try
            {
                switch (action)
                {
                    case "create":
                    {
                        // �������ػ��Ӵ�������ִ������
                        var spawnRequest = BuildSpawnRequest(
                            args, request, context, json, task!,
                            template, childLlmRoute, childCapability,
                            taskPlanning, permissionMode,
                            workingDirectory, originToolId,
                            parentContextSnapshot, delegation);
                        var createResult = await pool.CreateAsync(
                            args.PoolName, spawnRequest, ct);
                        return Success(
                            $"? �ػ��Ӵ��� '{args.PoolName}' �Ѵ�����",
                            new
                            {
                                status = createResult.Status.ToString(),
                                subSessionId = createResult.SubSessionId,
                                role = args.PoolRole ?? "(δָ��)",
                                hint = $"ʹ�� pool_name=\"{args.PoolName}\" (���� pool_action) ��ִ������",
                            });
                    }

                    case "destroy":
                    {
                        var destroyed = await pool.DestroyAsync(args.PoolName, ct);
                        return destroyed
                            ? Success($"? �ػ��Ӵ��� '{args.PoolName}' �����١�")
                            : Fail($"? �Ӵ��� '{args.PoolName}' �����ڻ������١�");
                    }

                    case "sleep":
                    {
                        var slept = await pool.SleepAsync(args.PoolName, ct);
                        return slept
                            ? Success($"? �ػ��Ӵ��� '{args.PoolName}' �����ߡ�")
                            : Fail($"? �Ӵ��� '{args.PoolName}' �����ڻ������١�");
                    }

                    case "list":
                    {
                        var agents = pool.List();
                        if (agents.Count == 0)
                            return Success("��Ϊ�ա�ʹ�� pool_name=\"<name>\" pool_action=\"create\" �����µĳػ��Ӵ�����");
                        var sb = new StringBuilder();
                        sb.AppendLine($"## �Ӵ�����״̬ ({agents.Count} ��)\n");
                        sb.AppendLine("| ���� | ״̬ | ��ɫ | ������ | ���ʹ�� | SubSessionId |");
                        sb.AppendLine("|------|------|------|--------|----------|-------------|");
                        foreach (var a in agents)
                            sb.AppendLine($"| {a.Name} | {a.Status} | {a.Role ?? "-"} | {a.TaskCount} | {a.LastUsedAt:HH:mm:ss} | {a.SubSessionId?.Substring(0, Math.Min(8, a.SubSessionId.Length))}... |");
                        return Success(sb.ToString());
                    }

                    case "cleanup":
                    {
                        // ���������ػ��Ӵ������ӳ������� + �����־û���¼
                        var subAgentManager = _services.GetService<ISubAgentManager>();
                        if (subAgentManager == null)
                            return Fail("? ISubAgentManager ����δע�ᡣ");

                        // �Ȼ�ȡ�Ӵ�����Ϣ���������־û���¼
                        var poolAgent = await pool.GetAsync(args.PoolName, ct);

                        // �ӳ�������
                        var destroyed = await pool.DestroyAsync(args.PoolName, ct);

                        // �����־û���¼
                        int dbCleaned = 0;
                        if (poolAgent != null)
                        {
                            dbCleaned = await subAgentManager.CleanupAsync(
                                request.SessionId,
                                new SubAgentCleanupFilter
                                {
                                    Status = args.CleanupStatus ?? "all",
                                    OlderThanDays = args.CleanupOlderThanDays,
                                    MaxCount = 1,
                                },
                                ct);
                        }

                        return Success(
                            $"? �Ӵ��� '{args.PoolName}' ��������" +
                            (destroyed ? " ����Ŀ�����١�" : " ����Ŀ�����ڻ������١�") +
                            $" �־û���¼������ {dbCleaned} ����");
                    }

                    case "cleanup-bulk":
                    {
                        // ���������������ڳػ��Ӵ����������������Ự�µ��Ӵ�����¼
                        var subAgentManager = _services.GetService<ISubAgentManager>();
                        if (subAgentManager == null)
                            return Fail("? ISubAgentManager ����δע�ᡣ");

                        var cleaned = await subAgentManager.CleanupAsync(
                            request.SessionId,
                            new SubAgentCleanupFilter
                            {
                                Status = args.CleanupStatus ?? "all",
                                OlderThanDays = args.CleanupOlderThanDays,
                                MaxCount = 100,
                            },
                            ct);

                        var filterDesc = args.CleanupStatus ?? "all";
                        if (args.CleanupOlderThanDays.HasValue)
                            filterDesc += $", older than {args.CleanupOlderThanDays} days";

                        return Success($"? �����������: {cleaned} ���Ӵ�����������ɸѡ: {filterDesc}����");
                    }

                    case "execute":
                    default:
                    {
                        // ִ�������Զ��������ã�
                        var execSpawnRequest = BuildSpawnRequest(
                            args, request, context, json, task!,
                            template, childLlmRoute, childCapability,
                            taskPlanning, permissionMode,
                            workingDirectory, originToolId,
                            parentContextSnapshot, delegation);
                        var result = await pool.ExecuteAsync(
                            args.PoolName, execSpawnRequest, ct);

                        // ��װΪ�ṹ�� JSON��ȷ���� SmartWorkflowToolBase.ExtractRawReport ����
                        var wrapped = JsonSerializer.Serialize(new
                        {
                            schema = "pudding-subagent-result",
                            version = 1,
                            subAgentId = result.SubSessionId,
                            runId = result.RunId,
                            status = result.Status,
                            rawOutput = result.Reply,
                            error = result.Error,
                        }, JsonOpts);

                        return new ToolExecutionResult
                        {
                            Success = result.Success,
                            Output = wrapped,
                            Error = result.Success ? null : result.Error,
                            ExitCode = result.Success ? 0 : 1,
                        };
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                // ����/æ/�����ڵ�ҵ���쳣
                return Fail(
                    $"? �ز���ʧ��: {ex.Message}\n\n��ʾ: ʹ�� pool_name=\"{args.PoolName}\" pool_action=\"list\" �鿴��ǰ��״̬��");
            }
        }

        // === ԭ��һ�����Ӵ����߼���pool_name Ϊ��ʱ�� ===

        if (batchTasksResult.Tasks is not null)
        {
            try
            {
                var batch = await subAgentInvocation.InvokeBatchAsync(new SubAgentBatchInvocationRequest
                {
                    ParentSessionId = request.SessionId,
                    ParentAgentInstanceId =
                        context.ConfigurationAgentInstanceId ?? request.AgentInstanceId,
                    ParentAgentId = request.AgentInstanceId,
                    WorkspaceId = request.WorkspaceId,
                    WorkingDirectory = workingDirectory,
                    TemplateId = template.TemplateId,
                    Tasks = batchTasksResult.Tasks,
                    IsAsync = !isSync,
                    LlmConfig = childLlmRoute.Config,
                                    LlmProfile = childLlmRoute.Profile,
                ParentContextSnapshot = parentContextSnapshot,
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
                    ParentExecutionDeadlineUtc = context.ExecutionDeadlineUtc,
                    BatchId = GetStringProp(json, "batch_id")
                           ?? GetStringProp(json, "batchId"),
                    OriginToolId = originToolId,
                    ParentExecutionIdentity = context.ExecutionIdentity,
                }, ct);

                return new ToolExecutionResult
                {
                    Success = batch.Status is "completed" or "running" or "budget_exhausted" or "partial_budget_exhausted",
                    Output = BuildBatchToolOutput(batch),
                    Error = batch.Error,
                    ExitCode = batch.Status is "completed" or "running" or "budget_exhausted" or "partial_budget_exhausted" ? 0 : 1,
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
                ParentAgentInstanceId =
                    context.ConfigurationAgentInstanceId ?? request.AgentInstanceId,
                ParentAgentId = request.AgentInstanceId,
                WorkspaceId = request.WorkspaceId,
                WorkingDirectory = workingDirectory,
                TemplateId = template.TemplateId,
                Task = task!,
                ResumeSubSessionId = resumeSubAgentId,
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
                ParentExecutionDeadlineUtc = context.ExecutionDeadlineUtc,
                InvocationId = GetStringProp(json, "invocation_id")
                            ?? GetStringProp(json, "invocationId"),
                OriginToolId = originToolId,
                ParentExecutionIdentity = context.ExecutionIdentity,
            }, ct);

            if (isSync)
            {
                var handled = invocationResult.Status is "completed" or "budget_exhausted";
                return new ToolExecutionResult
                {
                    Success = handled,
                    Output = BuildSingleToolOutput(invocationResult),
                    Error = handled ? null : invocationResult.Error,
                    ExitCode = handled ? 0 : 1,
                };
            }

            _logger.LogInformation(
                "[SubAgent] Async spawned sub={SubAgent} parent={Parent}",
                invocationResult.SubSessionId, request.SessionId);

            return Success(
                $"�첽�Ӵ����Ѵ�����sub_agent_id = {invocationResult.SubSessionId}��" +
                $"完成后通过 subagent_result 消息通道通知。",
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

    // ���� ˽�и��� ������������������������������������������������������������������������������������������������������������

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
            "The Runtime JSON control envelope remains mandatory. When the task is complete, return status=DONE, tool=null, and put exactly these top-level sections inside the message field, in this order:",
            "SUMMARY:",
            "CHANGES:",
            "EVIDENCE:",
            "RISKS:",
            "BLOCKERS:",
            "",
            "Do not emit the five sections outside the Runtime envelope. Evidence must use path:line references when source files are involved. If a section has no content, write \"none\"."
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
            runId = result.RunId,
            taskId = result.TaskId,
            status = result.Status,
            resumable = result.Status == "budget_exhausted",
            resumeSubAgentId = result.Status == "budget_exhausted" ? result.SubSessionId : null,
            summary = GetSectionOrFallback(sections, "SUMMARY", result.Reply),
            changes = SplitSectionList(sections.GetValueOrDefault("CHANGES")),
            evidence = SplitSectionList(sections.GetValueOrDefault("EVIDENCE")),
            risks = SplitSectionList(sections.GetValueOrDefault("RISKS")),
            blockers = SplitSectionList(sections.GetValueOrDefault("BLOCKERS")),
                        error = result.Error,
            rawOutput = result.Reply,
            rawOutputLength = result.Reply?.Length ?? 0,
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
            return Fail("����滮ί�ɲ���δע�ᣬ�޷������� task planning �����ĵ��Ӵ�����");
        }

        var plan = await taskStore.GetPlanAsync(planning.TaskPlanId!, ct);
        if (plan is null)
            return Fail($"����滮�ƻ������ڣ�{planning.TaskPlanId}");

        var node = await taskStore.GetNodeAsync(planning.TaskNodeId!, ct);
        if (node is null)
            return Fail($"����ڵ㲻���ڣ�{planning.TaskNodeId}");

        if (!string.Equals(node.PlanId, plan.PlanId, StringComparison.Ordinal))
            return Fail($"����ڵ� {node.TaskNodeId} �����ڼƻ� {plan.PlanId}��");

        var decision = await policy.CanAssignAsync(node, plan, TaskAssignmentKinds.SubAgent, ct);
        if (decision.Allowed)
            return null;

        return Fail(
            $"����滮���Ծܾ������Ӵ�����{decision.Reason} " +
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

        /// <summary>����ģ�� ID��֧�־�ȷƥ�� + ģ�����ˡ�</summary>
    private static AgentTemplateDefinition? ResolveTemplate(string templateId)
    {
        return BuiltInAgentTemplates.ResolveBest(templateId);
    }

    /// <summary>
    /// ���� SubAgentSpawnRequest�����ػ�·����Create/Execute��ʹ�á�
    /// ������һ����·����ͬ�Ľ��������ģ�塢LLM ·�ɡ��������Եȣ���
    /// </summary>
    private static SubAgentSpawnRequest BuildSpawnRequest(
        SubAgentToolArgs args,
        SubAgentToolRequest request,
        ToolExecutionContext context,
        JsonObject? json,
        string task,
        AgentTemplateDefinition template,
        ResolvedChildLlmRoute llmRoute,
        CapabilityPolicy capability,
        TaskPlanningSpawnContext taskPlanning,
        string permissionMode,
        string? workingDirectory,
        string originToolId,
        string? parentContextSnapshot,
        DelegationProtocolInput delegation)
    {
        return new SubAgentSpawnRequest
        {
            ParentSessionId = request.SessionId,
            ParentAgentId = request.AgentInstanceId,
            ConfigurationAgentInstanceId = context.ConfigurationAgentInstanceId,
            WorkspaceId = request.WorkspaceId,
            WorkingDirectory = workingDirectory,
            TaskDescription = task,
            TemplateId = template.TemplateId,
            LlmConfig = llmRoute.Config,
            LlmProfile = llmRoute.Profile,
            ParentContextSnapshot = parentContextSnapshot,
            CapabilityPolicy = capability,
            TaskPlanId = taskPlanning.TaskPlanId,
            TaskNodeId = taskPlanning.TaskNodeId,
            ParentTaskNodeId = taskPlanning.ParentTaskNodeId,
            DelegationDepth = (taskPlanning.DelegationDepth ?? 0) + 1,
            MaxDelegationDepth = taskPlanning.MaxDelegationDepth,
            RoleInPlan = args.PoolRole ?? taskPlanning.RoleInPlan,
            AllowSubDelegation = taskPlanning.AllowSubDelegation == true,
            AllowAgentCreation = taskPlanning.AllowAgentCreation,
            AssignedObjective = taskPlanning.AssignedObjective,
            ExpectedOutputContract = taskPlanning.ExpectedOutputContract,
            ParentExecutionDeadlineUtc = context.ExecutionDeadlineUtc,
            InvocationId = GetStringProp(json, "invocation_id")
                        ?? GetStringProp(json, "invocationId"),
            OriginToolId = originToolId,
            ParentExecutionIdentity = context.ExecutionIdentity,
        };
    }


    /// <summary>
    /// �Ӵ����������̳и��������ԣ����µ�����������
    /// ��������ͨ������ָ�� AllowedToolNames �Ӽ���
    /// </summary>
    private CapabilityPolicy BuildChildCapability(
        JsonObject? json,
        SubAgentToolRequest request,
        AgentTemplateDefinition template,
        string permissionMode)
    {
        var basePolicy = template.Capability ?? new CapabilityPolicy();

        // �������õĹ����Ӽ�
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

                // ���� none mode: zero tools, pure reasoning ����
        if (string.Equals(permissionMode, SubAgentPermissionModes.None, StringComparison.OrdinalIgnoreCase))
        {
            return basePolicy with
            {
                AllowShellExecution = false,
                AllowFileWrite = false,
                AllowNetworkAccess = false,
                AllowedToolNames = Array.Empty<string>(),
                DefaultToolNames = Array.Empty<string>(),
                RequiresGrantToolNames = Array.Empty<string>(),
            };
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
    /// ��ͳһ LLM Resolver ��ȡΨһ����Դ�Ѿ������õ� Provider/Model �����ÿ��գ�
    /// ����ֻ�����Ӵ����������壨ProfileId/Role����
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
                ["fallback_model"] = args.FallbackModel,
                ["reuse_parent_context"] = args.ReuseParentContext,
                ["resume_sub_agent_id"] = args.ResumeSubAgentId,
                ["tools"] = args.Tools,
                ["permission_mode"] = args.PermissionMode,
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
                ["origin_tool_id"] = args.OriginToolId,
                ["pool_name"] = args.PoolName,
                ["pool_action"] = args.PoolAction,
                ["pool_role"] = args.PoolRole,
                ["cleanup_status"] = args.CleanupStatus,
                ["cleanup_older_than_days"] = args.CleanupOlderThanDays,
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

    /// <summary>�Ӹ����������Ŀ��չ����Ӵ����̳е��������ַ�����</summary>
    /// <remarks>
    /// v2: ��̬�㣨L0-L2����� FullContent ԭ�ģ����֦����֤ KV-cache ǰ׺һ�£���
    /// ��̬������ժҪԪ���ݡ�
    /// </remarks>
    private static string BuildParentContextSnapshot(ContextAssemblySnapshot snapshot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("--- LAYER: INHERITED-CONTEXT ---");
        sb.AppendLine("[���������ĴӸ������Ự Fork���Ѽ�֦���Ƴ����ߵ��á�˼ά��������]");
        sb.AppendLine($"���Ự: {snapshot.SessionId}");
        sb.AppendLine($"��װʱ��: {snapshot.AssembledAt:O}");
        sb.AppendLine($"�� Token ��: {snapshot.TotalTokens}");
        sb.AppendLine($"��̬��ָ��(SHA-256): {snapshot.StaticLayersFingerprint ?? "��"}");
        if (!string.IsNullOrEmpty(snapshot.StaticLayersFingerprint))
            sb.AppendLine("�Ӵ����ɶԱ�������̬��ָ��ȷ�� KV-cache �Ƿ�����С�");
        sb.AppendLine();

        // ��̬�㣺ԭ����� FullContent
        var staticLayers = snapshot.Layers
            .Where(l => l.IsStatic && !string.IsNullOrWhiteSpace(l.FullContent))
            .ToList();
        if (staticLayers.Count > 0)
        {
            sb.AppendLine("## �̳о�̬�㣨���ֽ�һ�£���֤ KV-cache ���У�");
            sb.AppendLine();
            foreach (var layer in staticLayers)
            {
                sb.AppendLine($"--- LAYER: {layer.LayerName} ---");
                sb.AppendLine(layer.FullContent);
                sb.AppendLine();
            }
        }

        // ��̬�㣺�����ժҪ
        var dynamicLayers = snapshot.Layers
            .Where(l => !l.IsStatic && !string.IsNullOrWhiteSpace(l.ContentPreview))
            .ToList();
        if (dynamicLayers.Count > 0)
        {
            sb.AppendLine("## ��������̬��ժҪ");
            foreach (var layer in dynamicLayers)
            {
                sb.AppendLine($"- [{layer.LayerName}] ({layer.TokenCount} tokens): {TruncatePreview(layer.ContentPreview, 500)}");
            }
        }

        // P1: ��������� N �ֶԻ����������ݣ������Ϣȫ�� + ����ժҪ��
        if (snapshot.RecentMessages is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## �������Ի���ʷ����֦��");
            var recentCount = Math.Min(snapshot.RecentMessages.Count, 6);
            for (int i = 0; i < recentCount; i++)
            {
                var msg = snapshot.RecentMessages[i];
                sb.AppendLine($"[{msg.Role}]: {TruncatePreview(msg.Content, 1000)}");
            }
            if (snapshot.RecentMessages.Count > 6)
            {
                sb.AppendLine($"... (�� {snapshot.RecentMessages.Count} ����֦��Ϣ������Ϊ��� {recentCount} ��)");
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

    [ToolParam("Optional model route. Prefer the full 'providerId/modelId' form (look it up with list_llm_providers); a bare modelId registered under multiple providers fails resolution.")]
    public string? Model { get; init; }

    [ToolParam("Optional fallback model route tried once when the requested model route fails to resolve (route-resolution failure only). Default 'bigmodel/glm-5.3-flash'. Set equal to model to disable the fallback.")]
    public string? FallbackModel { get; init; }

    [ToolParam("复用父代理的上下文环境（Fork + 分支 + 注入到子代理上下文），默认 false")]
    public bool? ReuseParentContext { get; init; }

    [ToolParam("Existing sub-agent id to resume with preserved context and a fresh Pudding-managed budget. Do not combine with tasks or pool_name.")]
    public string? ResumeSubAgentId { get; init; }

    [ToolParam("Optional comma-separated allowed tool id subset for the child agent.")]
    public string? Tools { get; init; }

    [ToolParam("Permission inheritance mode: inherit or low.")]
    public string? PermissionMode { get; init; }

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

    [ToolParam("Stable id of the parent tool that initiated this delegation.")]
    public string? OriginToolId { get; init; }

    /// <summary>
    /// 池化子代理名称。指定时使用池化配置，保持会话延续与 KV-cache 复用；
    /// 否则创建一个新子代理（每次新建会话）
    /// </summary>
    [ToolParam("池化子代理名称。指定时使用池化配置，否则创建一个新子代理")]
    public string? PoolName { get; init; }

    /// <summary>
    /// 池操作: create(创建并执行), execute(执行任务,默认), destroy(销毁子代理), sleep(休眠), list(列出池状态)
    /// </summary>
    [ToolParam("池操作: create, execute(默认), destroy, sleep, list, cleanup, cleanup-bulk")]
    public string? PoolAction { get; init; }

    /// <summary>
    /// 池化子代理角色（可选），用于区分用途：如 dev-agent, reviewer, explorer
    /// </summary>
    [ToolParam("池化子代理角色（可选），用于区分用途")]
    public string? PoolRole { get; init; }

    [ToolParam("清理筛选状态: failed, completed, all（用于 cleanup/cleanup-bulk）")]
    public string? CleanupStatus { get; init; }

    [ToolParam("清理超过 N 天的子代理（用于 cleanup-bulk）")]
    public int? CleanupOlderThanDays { get; init; }
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
