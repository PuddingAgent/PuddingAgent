using System.Text.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
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
                 "model（可选，必须是 providerId/modelId 完整路由，如 deepseek/deepseek-v3；" +
                 "多 provider 注册的裸 modelId 会报错，先用 list_llm_providers 查 route，不指定则用平台默认模型）、" +
                 "fallback_model（可选，模型路由解析失败时自动回退一次并在结果中告警，默认 deepseek/deepseek-flash；与 model 相同则禁用回退）、" +
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
    private const string DefaultFallbackModelRoute = "deepseek/deepseek-flash";

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

        // === 取消运行中的子代理（缺陷 D1 的 Agent 侧入口）===
        // 刻意放在 task 解析与池化分支之前：按 subSessionId 定位，不依赖 task，也不依赖 pool_name。
        // 教训：本分支最初被放在 task 校验之后，导致「仅传 pool_action=cancel」永远被参数校验拦下而不可达。
        if (string.Equals(args.PoolAction?.Trim(), "cancel", StringComparison.OrdinalIgnoreCase))
        {
            var canceller = _services.GetService<ISubAgentManager>();
            if (canceller is null)
                return Fail("? ISubAgentManager 服务未注册。");

            var targetSubAgentId = args.SubAgentId?.Trim();
            if (string.IsNullOrWhiteSpace(targetSubAgentId))
                return Fail("取消子代理需要 sub_agent_id。先用 query_sub_agents 查看运行中的子代理及其 id。");

            var cancelReason = string.IsNullOrWhiteSpace(args.CancelReason)
                ? "Cancelled by parent agent"
                : args.CancelReason!.Trim();

            var cancelAccepted = await canceller.CancelAsync(targetSubAgentId, cancelReason, ct);
            return cancelAccepted
                ? Success(
                    $"? 子代理 {targetSubAgentId} 已取消。",
                    new { sub_agent_id = targetSubAgentId, status = "cancelled", reason = cancelReason })
                : Fail($"? 子代理 {targetSubAgentId} 不存在，或已结束（无运行中的执行可取消）。");
        }

        // === 查看子代理运行日志（事件 + 工具审计尾部）===
        // 与 cancel 同样放在 task 解析之前：按 sub_agent_id 定位，不依赖 task，也不依赖 pool_name。
        if (string.Equals(args.PoolAction?.Trim(), "logs", StringComparison.OrdinalIgnoreCase))
        {
            var logTarget = args.SubAgentId?.Trim();
            if (string.IsNullOrWhiteSpace(logTarget))
                return Fail("查看子代理日志需要 sub_agent_id。先用 query_sub_agents 列出子代理及其 id。");

            var dataPaths = _services.GetService<PuddingDataPaths>();
            if (dataPaths is null)
                return Fail("? PuddingDataPaths 服务未注册。");

            var snapshot = SubAgentRunLogReader.ReadLatest(dataPaths, logTarget, args.LogsTail ?? 20);
            if (snapshot is null)
                return Fail($"? 找不到 sub_agent_id={logTarget} 的运行归档（可能尚未创建或已被清理）。");

            return Success(
                snapshot.ToDisplayText(),
                new
                {
                    sub_agent_id = logTarget,
                    run_id = snapshot.RunId,
                    status = snapshot.Status,
                    events = snapshot.EventCount,
                    tool_calls = snapshot.ToolCallCount,
                });
        }

        // === 给运行中的子代理发一条消息 ===
        // 与 cancel / logs 同样放在 task 解析之前：按 sub_agent_id 定位，不依赖 task，也不依赖 pool_name。
        if (string.Equals(args.PoolAction?.Trim(), "steer", StringComparison.OrdinalIgnoreCase))
        {
            // 2026-11-15：参数校验前置到服务解析之前。
            // 校验不需要服务实例，而先解析会让测试容器里的“服务未注册”提前返回，
            // 把后面的参数错误全部遮住（旧的 reachability 用例就是这样碰巧通过的）。
            var steerTarget = args.SubAgentId?.Trim();
            if (string.IsNullOrWhiteSpace(steerTarget))
                return Fail("给子代理发消息需要 sub_agent_id。先用 query_sub_agents 列出运行中的子代理及其 id。");

            var steerText = GetStringProp(json, "steer_message")
                         ?? GetStringProp(json, "message")
                         ?? args.SteerMessage;
            if (string.IsNullOrWhiteSpace(steerText))
                // 错误必须点名**工具 schema 里真实存在**的参数名：
                // spawn_sub_agent 只声明 steer_message，写成 message 会让模型照提示传一个被丢弃的参数，
                // 于是看到同一条错误再来一次，形成死循环（2026-11-15 实测踩到）。
                return Fail("给子代理发消息需要 steer_message（要注入的正文）。");

            var steerManager = _services.GetService<ISubAgentManager>();
            if (steerManager is null)
                return Fail("? ISubAgentManager 服务未注册。");

            var steered = await steerManager.SteerAsync(
                steerTarget, steerText!.Trim(), context.AgentInstanceId, ct);

            return steered.Outcome switch
            {
                PuddingCode.SubAgents.SubAgentSteerOutcome.Queued => Success(
                    $"? 已投递给子代理 {steerTarget}，它会在下一轮 LLM 调用前收到。",
                    new { sub_agent_id = steerTarget, steering_id = steered.SteeringId, status = "queued" }),
                // 不静默入队：写给已结束运行的消息没人会消费，只会躺到 TTL 过期。
                PuddingCode.SubAgents.SubAgentSteerOutcome.NotRunning => Fail(
                    $"? 子代理 {steerTarget} 当前不在运行，消息不会被消费，因此未提交。先用 query_sub_agents 确认状态。"),
                PuddingCode.SubAgents.SubAgentSteerOutcome.TargetUnresolved => Fail(
                    $"? 子代理 {steerTarget} 的运行清单缺少定位字段（workspace / session / 父 turn），为避免投错主体已拒绝。{steered.Detail}"),
                _ => Fail($"? 投递失败：{steered.Detail}"),
            };
        }

        // === 池管理动作（全局只读 + 条目生命周期）===
        // 与 cancel / logs / steer 同理放在 task 解析之前：这三条都不消费任务正文。
        // 教训（2026-11-24 实测）：它们曾被「task 必填」挡在门外——连只读的 list 都要传一个假任务。
        // 更糟的是 destroy / sleep 在旧位置会先过委派深度与并发守卫，等于「想销毁条目却先被创建类守卫拒绝」。
        // 另外，给了 pool_action 却漏了 pool_name 时旧代码会静默落进一次性派发分支（写错参数等于没事发生）。
        var managementAction = args.PoolAction?.Trim().ToLowerInvariant();
        if (managementAction is "list" or "destroy" or "sleep" or "cleanup" or "cleanup-bulk")
        {
            var managementPool = _services.GetService<ISubAgentPool>();
            if (managementPool is null)
                return Fail("? SubAgentPool 服务未注册。请检查 DI 配置。");

            // 归属范围：普通 Agent 只能操作自己会话的条目。
            // 池是按会话隔离的（归属写库、派活时校验），列表/休眠/销毁若走全局入口，
            // 就会出现「看得见另一个会话的成员，还能让它休眠或销毁」——
            // 而同一个条目的派活又会被归属校验拒掉，两个入口对同一件事给出相反结论。
            // 跨会话操作是管理面的能力，走 admin REST，不走这个工具。
            var ownerScope = new PoolOwnerScope(request.SessionId, request.WorkspaceId);

            // list 不需要条目名；cleanup-bulk 按父会话整批清理，也不需要。
            if (managementAction is not ("list" or "cleanup-bulk") && string.IsNullOrWhiteSpace(args.PoolName))
                return Fail($"pool_action={managementAction} 需要 pool_name。先用 pool_action=list 查看可用条目名。");

            try
            {
                switch (managementAction)
                {
                    case "list":
                    {
                        // 未就绪时 ListOwnedBy() 按契约抛 PoolNotReadyException，**不返回空集合**——
                        // 因为「还没加载完」与「本会话确实没有条目」处置相反（前者等，后者创建）。
                        // 未就绪由下面的 catch 渲染成「池未就绪 / 恢复失败」，不会伪装成空池。
                        // 用声明版而不是同步版：面板改角色写的是库，同步版返回启动快照——
                        // 只刷新派发路径会让「库 / 派发 / 列表」三个面各说一套。
                        var agents = await managementPool.ListOwnedByDeclaredAsync(ownerScope, ct);
                        if (agents.Count == 0)
                            return Success("本会话没有池化子代理（其它会话的成员不在此列出）。" +
                                "使用 pool_name=\"<name>\" pool_action=\"create\" 创建新的池化子代理。");
                        return Success(PoolListingFormatter.Format(agents));
                    }

                    case "cleanup":
                    {
                        // 清理单个池化子代理：先清持久化记录并复核，再销毁池条目。
                        var subAgentManager = _services.GetService<ISubAgentManager>();
                        if (subAgentManager is null)
                            return Fail("? ISubAgentManager 服务未注册。");

                        // 定位键：名字能改、条目销毁后连查都查不到，稳定键只能是 SubSessionId。
                        // 先问池；条目已销毁时退回调用方给的 sub_agent_id——否则「清理失败」不可重试。
                        var poolAgent = await managementPool.GetAsync(args.PoolName, ct);
                        var targetSubSessionId = poolAgent?.SubSessionId;
                        if (string.IsNullOrWhiteSpace(targetSubSessionId))
                            targetSubSessionId = string.IsNullOrWhiteSpace(args.SubAgentId)
                                ? null
                                : args.SubAgentId.Trim();

                        if (targetSubSessionId is null)
                            return Fail(
                                DescribePoolEntryMissing(args.PoolName, "cleanup") +
                                "\n提示：条目已销毁时改用 pool_action=\"cleanup\" " +
                                "sub_agent_id=\"<SubSessionId>\" 重试（SubSessionId 可用 pool_action=\"list\" 查到），" +
                                "或用 pool_action=\"cleanup-bulk\" 按父会话批量清理。");

                        // 顺序有两种，不能通用：
                        // · 忙碌条目必须先销毁——销毁会级联取消在跑的那一单，而清理会跳过「运行中」的记录，
                        //   先清只会清 0 条，并在下面的复核里被判成失败。
                        // · 空闲条目必须先清理、复核通过再销毁——反过来一旦清理失败，条目没了，
                        //   唯一稳定定位键也就没了，重试只能清 0 条却报「已清理」。
                        var wasBusy = poolAgent?.Status == PooledSubAgentStatus.Busy;
                        var destroyed = false;
                        if (wasBusy)
                            destroyed = await managementPool.DestroyAsync(args.PoolName, ownerScope, ct);

// 运行归档 id 必须在清理**之前**取：清理会删掉 sub_agent_runs 行，
                        // 之后快照里的 RunId 会退回 SubSessionId 这个占位值，查归档恒为「不存在」——
                        // 于是「删成功」与「一条没删」在事后无法区分。
                        var runArchive = _services.GetService<ISubAgentRunStore>();
                        var runId = await ResolveRunArchiveIdAsync(subAgentManager, targetSubSessionId, ct);

                        // 清理持久化记录：按 SubSessionId 精确清理。只传父会话 + MaxCount=1 时，
                        // 候选集是该会话下所有非运行中的子代理，删到哪一条取决于返回顺序。
                        var dbCleaned = await subAgentManager.CleanupAsync(
                            request.SessionId,
                            new SubAgentCleanupFilter
                            {
                                SubSessionId = targetSubSessionId,
                                Status = args.CleanupStatus ?? "all",
                                OlderThanDays = args.CleanupOlderThanDays,
                                MaxCount = 1,
                            },
                            ct);

                        // 复核对象必须是**被删的那个对象**：运行归档。
                        // CleanupAsync 只删 sub_agent_runs 行与磁盘归档，session_sub_agents 投影行本来就不删；
                        // 拿投影做验收时这条断言恒为真，于是每次清理都报「没清掉」（2026-11-25 已实测的回归）。
                        var archivePresent = await ProbeRunArchiveAsync(runArchive, runId, ct);

                        if (archivePresent == false && !destroyed)
                            destroyed = await managementPool.DestroyAsync(args.PoolName, ownerScope, ct);

                        // 三个分支的下一步动作不同，不得合并：无法复核要去查服务注册，
                        // 仍在要原地重试，已删才是完成。
                        if (archivePresent is null)
                            return Fail(
                                $"? 子代理 '{args.PoolName}' 的运行归档无法复核" +
                                $"（runId={runId}，ISubAgentRunStore 不可用，清理返回 {dbCleaned} 条）。" +
                                (destroyed ? "池条目已销毁。" : "池条目已保留。") +
                                "\n提示：这是可重试状态，原地再调一次 cleanup 即可" +
                                $"（条目已销毁时用 sub_agent_id=\"{targetSubSessionId}\"）。");

                        if (archivePresent == true)
                            return Fail(
                                $"? 子代理 '{args.PoolName}' 的运行归档没删掉" +
                                $"（runId={runId}，SubSessionId={targetSubSessionId}，清理返回 {dbCleaned} 条）。" +
                                (destroyed ? "池条目已销毁。" : "池条目已保留。") +
                                "\n提示：这是可重试状态，原地再调一次 cleanup 即可" +
                                $"（条目已销毁时用 sub_agent_id=\"{targetSubSessionId}\"）。");

                        return Success(
                            $"? 子代理 '{args.PoolName}' 已清理。" +
                            (destroyed ? " 池条目已销毁。" : " 池条目不存在或已销毁。") +
                            (dbCleaned > 0
                                ? $" 已删除运行归档 {dbCleaned} 条。"
                                : " 运行归档本就不存在（此前已清理或从未落盘）。"));
                    }

                    case "cleanup-bulk":
                    {
                        // 批量清理：不限于池化子代理，清理整个父会话下的子代理记录
                        var subAgentManager = _services.GetService<ISubAgentManager>();
                        if (subAgentManager is null)
                            return Fail("? ISubAgentManager 服务未注册。");

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

                        return Success($"? 批量清理完成: {cleaned} 个子代理已清理（筛选: {filterDesc}）。");
                    }

                    case "destroy":
                        // false 是「确认没有这个条目」，与「未就绪（还不知道有没有）」是相反的事实，
                        // 所以单独给「未命中 + 下一步」，不并进通用失败文案。
                        // 归属不符会抛 PoolOwnershipMismatchException（由下面的 catch 渲染）。
                        return await managementPool.DestroyAsync(args.PoolName, ownerScope, ct)
                            ? Success($"? 池化子代理 '{args.PoolName}' 已销毁。")
                            : Fail(DescribePoolEntryMissing(args.PoolName, "destroy"));

                    default: // sleep
                        // 同 destroy：false 表示名字未命中或条目已死，不是「池不可用」。
                        return await managementPool.SleepAsync(args.PoolName, ownerScope, ct)
                            ? Success($"? 池化子代理 '{args.PoolName}' 已休眠。")
                            : Fail(DescribePoolEntryMissing(args.PoolName, "sleep"));
                }
            }
            catch (InvalidOperationException ex)
            {
                // 与主 switch 的 catch 同一渲染，保证未就绪 / 归属 / 忙 / 池满四类文案不因入口不同而漂移。
                return Fail(DescribePoolFailure(ex, args.PoolName)
                    ?? $"? 池操作失败: {ex.Message}\n\n提示: 使用 pool_name=\"{args.PoolName}\" pool_action=\"list\" 查看当前池状态。");
            }
        }

        var task = GetStringProp(json, "task")
                ?? GetStringProp(json, "prompt")
                ?? request.Parameters.GetValueOrDefault("task");
        if (delegation.HasAnyField)
            task = RenderDelegationTask(task, delegation);
        var batchTasksResult = TryReadBatchTasks(json);
        var resumeSubAgentId = GetStringProp(json, "resume_sub_agent_id")
                            ?? GetStringProp(json, "resumeSubAgentId")
                            ?? args.ResumeSubAgentId;

        // 人类可读身份：name / avatar / responsibility 三个参数都是可选的。
        // 这里只负责取值，不做"没给就编一个"——默认名与重名去重必须发生在写入点
        // （SessionStateManager 同一次查询、同一次 UPSERT），否则池化复用会把已设名字冲掉。
        var displayName = GetStringProp(json, "name")
                       ?? GetStringProp(json, "display_name")
                       ?? request.Parameters.GetValueOrDefault("name")
                       ?? args.Name;
        var avatarKey = GetStringProp(json, "avatar")
                     ?? GetStringProp(json, "avatar_key")
                     ?? request.Parameters.GetValueOrDefault("avatar")
                     ?? args.Avatar;
        var responsibility = GetStringProp(json, "responsibility")
                          ?? request.Parameters.GetValueOrDefault("responsibility")
                          ?? args.Responsibility;

        var hasBatchTasksArgument = json.TryGetPropertyValue("tasks", out var tasksNode)
                                    && tasksNode is not null;
        if (!string.IsNullOrWhiteSpace(resumeSubAgentId) && hasBatchTasksArgument)
            return Fail("resume_sub_agent_id can only be used with a single task, not tasks batch mode.");
        if (!string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is not null)
            return Fail("参数 'task' 和 'tasks' 必须二选一，不能同时传入。");
        if (string.IsNullOrWhiteSpace(task) && batchTasksResult.Tasks is null)
            return Fail("参数 'task' 或 'tasks' 是必需的。批量模式必须传入 JSON array。");
        if (batchTasksResult.Error is not null)
            return Fail(batchTasksResult.Error);
        if (!string.IsNullOrWhiteSpace(resumeSubAgentId) && !string.IsNullOrWhiteSpace(args.PoolName))
            return Fail("resume_sub_agent_id cannot be combined with pool_name; pooled agents already own a reusable session identity.");

        // 模板解析：显式优先 → 池条目声明的模板 → 工具默认值。
        // 继承不能省：模板决定子代理的提示词、可用工具与能力上限。池复用只回写 ReuseSubSessionId，
        // 若模板也退回默认，界面上那个成员「是谁」就只剩名字——建的时候是 codex、派下去变成
        // workspace-task-agent，而结果里看不出差异。与 model 继承同一处置：显式值仍优先。
        var explicitTemplateId = GetStringProp(json, "agent_template")
                              ?? GetStringProp(json, "template")
                              ?? request.Parameters.GetValueOrDefault("template");

        // 未显式指定模板时的取值优先级：池条目声明 > 工具默认。
        // 继承查询先做：条目不存在 / 池未就绪都只返回 null，不阻断派活。
        string? declaredPoolTemplate = null;
        if (string.IsNullOrWhiteSpace(explicitTemplateId) && !string.IsNullOrWhiteSpace(args.PoolName))
        {
            var poolForTemplate = _services.GetService<ISubAgentPool>();
            if (poolForTemplate is not null)
            {
                try
                {
                    declaredPoolTemplate = await poolForTemplate.GetDeclaredTemplateIdAsync(args.PoolName, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 继承失败不阻断派活：条目不存在 / 池未就绪由后面的 ExecuteAsync 统一渲染。
                    _logger.LogDebug(ex, "[SubAgent] 池条目模板继承查询失败 name={Name}", args.PoolName);
                }
            }
        }

        var inheritedPoolTemplateId = string.IsNullOrWhiteSpace(explicitTemplateId)
                                   && !string.IsNullOrWhiteSpace(declaredPoolTemplate);
        var templateId = !string.IsNullOrWhiteSpace(explicitTemplateId)
            ? explicitTemplateId
            : inheritedPoolTemplateId
                ? declaredPoolTemplate!
                : "workspace-task-agent";

        var isSync = GetBoolProp(json, "sync")
                  ?? (request.Parameters.TryGetValue("sync", out var syncVal)
                        && bool.TryParse(syncVal, out var syncBool) && syncBool);

        // 没有显式指定 sync → 默认同步
        if (!HasProp(json, "sync") && !request.Parameters.ContainsKey("sync"))
            isSync = true;

                var modelId = GetStringProp(json, "model")
                   ?? request.Parameters.GetValueOrDefault("model");
        // 2026-11-21 (P1)：续跑必须继承原子代理的模型路由。
        // 旧行为：resume 不带 model 时直接走平台默认 —— 实测被解析为 deepseek-flash，
        // 于是「续跑」变成「换模型续跑」：结果不可比、用量归因错位、跨 provider 复用零缓存收益。
        // 只覆盖「未显式指定 model」且「允许模型选择」的情形；显式 model 视为调用方明确意图。
        // 刻意不 fail-closed：历史行 model_id 可能为空，宿主也可能未注册 ISessionStateManager，
        // 硬失败会打断合法续跑。改为「能继承就继承 + 无法继承必须显式告警」，把静默换成可见。
        var inheritedModelRoute = false;
        if (!string.IsNullOrWhiteSpace(resumeSubAgentId)
            && string.IsNullOrWhiteSpace(modelId)
            && ResolveAllowModelSelection())
        {
            var originalModelRoute = await TryFindResumedModelRouteAsync(resumeSubAgentId, request.SessionId, ct);
            if (string.IsNullOrWhiteSpace(originalModelRoute))
            {
                routeAdvisories.Add(
                    $"⚠️ [spawn_sub_agent] 续跑未能继承原模型路由（子代理 '{resumeSubAgentId}' 没有记录 model_id，" +
                    "或会话状态层不可用），本次将使用平台默认路由 —— 续跑结果可能与首轮不可比。" +
                    "需要确定性请显式传 model。");
            }
            else
            {
                modelId = originalModelRoute;
                inheritedModelRoute = true;
            }
        }

        // 2026-11-24 (P2)：池化执行必须继承条目声明的模型路由。
        // 与续跑继承是同一个理由：条目上的 model_id 是「这个成员固定用哪个模型」的唯一记录，
        // 执行路径不读它，界面上设成 Claude 也会被平台默认路由盖掉，而使用者从结果里看不出差异
        // —— 那比不提供这个设置更糟。显式 model 仍优先（调用方明确意图）；条目没声明时退回平台默认。
        var inheritedPoolModelRoute = false;
        if (!string.IsNullOrWhiteSpace(args.PoolName)
            && string.IsNullOrWhiteSpace(modelId)
            && ResolveAllowModelSelection())
        {
            var poolForRoute = _services.GetService<ISubAgentPool>();
            if (poolForRoute is not null)
            {
                try
                {
                    var declared = await poolForRoute.GetDeclaredModelIdAsync(args.PoolName, ct);
                    if (!string.IsNullOrWhiteSpace(declared))
                    {
                        modelId = declared;
                        inheritedPoolModelRoute = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // 继承失败不阻断派活：条目不存在 / 池未就绪由 ExecuteAsync 统一渲染。
                    _logger.LogDebug(ex, "[SubAgent] 池条目模型继承查询失败 name={Name}", args.PoolName);
                }
            }
        }

        var fallbackModel = GetStringProp(json, "fallback_model")
                        ?? GetStringProp(json, "fallbackModel")
                        ?? request.Parameters.GetValueOrDefault("fallback_model");
        var permissionMode = GetStringProp(json, "permission_mode")
                          ?? GetStringProp(json, "permissionMode")
                          ?? SubAgentPermissionModes.Inherit;
        var originToolId = GetStringProp(json, "origin_tool_id")
                        ?? GetStringProp(json, "originToolId")
                        ?? "spawn_sub_agent";
        // 2026-11-14：未显式指定 working_directory 时，继承父代理的工作目录。
        // 旧行为回落到工作区根（D:\Data\workspaces\<ws>），而仓库常在 D:\CodeProject\...，
        // 于是子代理的相对路径全部失效，只能靠任务书里写绝对路径（§九实测：一次 run 把
        // dir 结果写到了工作区 temp 而不是仓库 temp，就是这个原因）。
        var workingDirectory = ResolveWorkingDirectory(
            GetStringArg(json, request, "working_directory", "workingDirectory"),
            context.WorkingDirectory);
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
        if (inheritedModelRoute)
        {
            routeAdvisories.Add(
                $"ℹ️ [spawn_sub_agent] 续跑已继承原模型路由 " +
                $"'{childLlmRoute.Profile.ProviderId}/{childLlmRoute.Profile.ModelId}'（未显式指定 model）。");
        }
        if (inheritedPoolModelRoute)
        {
            routeAdvisories.Add(
                $"ℹ️ [spawn_sub_agent] 池条目继承了声明的模型路由 " +
                $"'{childLlmRoute.Profile.ProviderId}/{childLlmRoute.Profile.ModelId}'（未显式指定 model）。");
        }
        if (inheritedPoolTemplateId)
        {
            // 模板不是展示字段：不告的话使用者看不到自己拿到的是哪个身份的子代理。
            routeAdvisories.Add(
                $"ℹ️ [spawn_sub_agent] 池条目继承了声明的模板 '{template.TemplateId}'（未显式指定 agent_template）。");
        }

        var taskPlanning = ReadTaskPlanningContext(json, request, task ?? batchTasksResult.Tasks![0].Task);
        var policyDeny = await CheckTaskPlanningPolicyAsync(taskPlanning, ct);
        if (policyDeny is not null)
            return policyDeny;

        // 平台级委派深度守卫：必须位于所有创建路径（sync/async/pool/batch）之前，
        // 且位于 cancel/logs 分支之后 —— 那两条是管理操作，不是创建。
        var depthDeny = await CheckDelegationDepthAsync(request.SessionId, ct);
        if (depthDeny is not null)
            return depthDeny;

        // 需求 5：同一主 Agent 下所有递归层级同时存活的子代理总数上限。
        var concurrencyDeny = await CheckConcurrencyLimitAsync(request.SessionId, ct);
        if (concurrencyDeny is not null)
            return concurrencyDeny;

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

        // === 池化子代理路由 ===
        // 当 pool_name 非空时，走池化复用路径；否则走原有一次性子代理逻辑。
        if (!string.IsNullOrWhiteSpace(args.PoolName))
        {
            var pool = _services.GetService<ISubAgentPool>();
            if (pool == null)
                return ToolExecutionResult.Fail(
                    "? SubAgentPool 服务未注册。请检查 DI 配置。");

            // 批量模式不支持池化（语义冲突）
            if (batchTasksResult.Tasks is not null)
                return Fail("批量任务模式 (tasks) 不支持池化子代理。请使用单任务 (task) + pool_name。");

            var action = args.PoolAction?.ToLowerInvariant() ?? "execute";

            try
            {
                switch (action)
                {
                    case "create":
                    {
                        // 仅创建池化子代理，不执行任务
                        var spawnRequest = BuildSpawnRequest(
                            args, request, context, json, task!,
                            template, childLlmRoute, childCapability,
                            taskPlanning, permissionMode,
                            workingDirectory, originToolId,
                            parentContextSnapshot, delegation);
                        // 池化创建也必须过预算守卫：池化子代理同样消费派生预算。
                        if (DescribeParentBudgetInfeasibility(context) is { } poolCreateBudgetGuardError)
                            return Fail(poolCreateBudgetGuardError);

                        var createResult = await pool.CreateAsync(
                            args.PoolName, spawnRequest, ct);
                        return Success(
                            $"? 池化子代理 '{args.PoolName}' 已创建。",
                            new
                            {
                                status = createResult.Status.ToString(),
                                subSessionId = createResult.SubSessionId,
                                role = args.PoolRole ?? "(未指定)",
                                hint = $"使用 pool_name=\"{args.PoolName}\" (不带 pool_action) 来执行任务。",
                            });
                    }

                    case "execute":
                    default:
                    {
                        // 执行任务（自动创建或复用）
                        var execSpawnRequest = BuildSpawnRequest(
                            args, request, context, json, task!,
                            template, childLlmRoute, childCapability,
                            taskPlanning, permissionMode,
                            workingDirectory, originToolId,
                            parentContextSnapshot, delegation);
                        // 池化执行也必须过预算守卫：池化子代理同样消费派生预算。
                        if (DescribeParentBudgetInfeasibility(context) is { } poolExecuteBudgetGuardError)
                            return Fail(poolExecuteBudgetGuardError);

                        var result = await pool.ExecuteAsync(
                            args.PoolName, execSpawnRequest, ct);

                        // 包装为结构化 JSON，确保与 SmartWorkflowToolBase.ExtractRawReport 兼容
                        var wrapped = JsonSerializer.Serialize(new
                        {
                            schema = "pudding-subagent-result",
                            version = 1,
                            subAgentId = result.SubSessionId,
                            runId = result.RunId,
                            status = result.Status,
                            rawOutput = result.Reply,
                            error = result.Error,
                            // 池化派活可能继承了成员声明的角色；不回传的话调用方只能看到自己的入参，
                            // 而「没传」恰恰是继承的触发条件 —— 生效值只有这里能说清。
                            roleInPlan = result.RoleInPlan,
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
                // 已知的池失败按类别渲染（未就绪 / 归属不匹配 / 忙 / 池满），每类给出不同的下一步；
                // 其余 InvalidOperationException（例如「同名条目已存在」）保持既有通用文案——
                // 既不吞掉，也不硬塞进某个类别里错报。
                //
                // 非 InvalidOperationException（取消、参数错误等）刻意不在此捕获：它们不是池的业务失败，
                // 包装成「池操作失败」只会掩盖真实原因，交由上层既有路径处理。
                return Fail(
                    DescribePoolFailure(ex, args.PoolName)
                    ?? $"? 池操作失败: {ex.Message}\n\n提示: 使用 pool_name=\"{args.PoolName}\" pool_action=\"list\" 查看当前池状态。");
            }
        }

        // === 原有一次性子代理逻辑（pool_name 为空时） ===

        if (batchTasksResult.Tasks is not null)
        {
            // 委派边界守卫：父级派生预算已耗尽/低于可执行下限时直接拒绝，不派发注定首轮即死的子代理。
            if (DescribeParentBudgetInfeasibility(context) is { } budgetGuardError)
                return Fail(budgetGuardError);

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
                    UsageBudget = context.UsageBudget,
                }, ct);

                return new ToolExecutionResult
                {
                    Success = batch.Status is "completed" or "running" or "budget_exhausted" or "partial_budget_exhausted",
                    Output = BuildBatchToolOutput(batch),
                    Error = batch.Error,
                    ExitCode = batch.Status is "completed" or "running" or "budget_exhausted" or "partial_budget_exhausted" ? 0 : 1,
                    DelegatedUsage = isSync ? SumUsage(batch.Results) : null,
                };
            }
            catch (InvalidOperationException ex)
            {
                return Fail(ex.Message);
            }
        }

        // 委派边界守卫：父级派生预算已耗尽/低于可执行下限时直接拒绝，不派发注定首轮即死的子代理。
        if (DescribeParentBudgetInfeasibility(context) is { } singleBudgetGuardError)
            return Fail(singleBudgetGuardError);

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
                DisplayName = displayName,
                AvatarKey = avatarKey,
                Responsibility = responsibility,
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
                UsageBudget = context.UsageBudget,
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
                    DelegatedUsage = invocationResult.Usage,
                };
            }

            _logger.LogInformation(
                "[SubAgent] Async spawned sub={SubAgent} parent={Parent}",
                invocationResult.SubSessionId, request.SessionId);

            return Success(
                $"异步子代理已创建。sub_agent_id = {invocationResult.SubSessionId}。" +
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

    private async Task<ToolExecutionResult?> CheckDelegationDepthAsync(
        string? sessionId,
        CancellationToken ct)
    {
        var maxDepth = ResolveDelegationMaxDepth();
        if (maxDepth <= 0)
            return null;

        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var runStore = _services.GetService<ISubAgentRunStore>();
        if (runStore is null)
        {
            // fail-open：基础设施未注册时不能把整条派发能力掰断；但必须留下可检索的痕迹。
            _logger.LogWarning(
                "[SubAgent] 委派深度守卫跳过：ISubAgentRunStore 未注册（session={Session}）",
                sessionId);
            return null;
        }

        bool isSubAgentSession;
        try
        {
            isSubAgentSession = await runStore.HasSubAgentAncestryAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            // 与上面同理：查询失败不等于「它是子代理」，不因此拦截正常派发。
            _logger.LogWarning(ex, "[SubAgent] 委派深度守卫查询失败，放行（session={Session}）", sessionId);
            return null;
        }

        if (!isSubAgentSession)
            return null;

        return Fail(
            $"子代理不得再创建子代理：平台配置 SubAgentDelegation:MaxDepth={maxDepth}，" +
            "而当前会话本身已经是子代理会话（该会话由平台创建的运行归档记录，不受 depth 参数影响）。" +
            "如需多层委派，将 SubAgentDelegation:MaxDepth 调大（当前实现仅支持 1）。");
    }

    /// <summary>
    /// 读取平台级委派深度上限。默认 1 = 仅主 Agent 可创建子代理。
    /// 0 或负数 = 不限制（仍保留留给运维的逃生口）。
    /// </summary>
    private int ResolveDelegationMaxDepth()
    {
        var configuration = _services.GetService<IConfiguration>();
        var raw = configuration?["SubAgentDelegation:MaxDepth"];
        return int.TryParse(raw, out var value) ? value : 1;
    }

    /// <summary>
    /// 需求 5：同一主 Agent 下**所有递归层级**同时存活的子代理总数守卫。
    /// 配置 SubAgentDelegation:MaxConcurrentSubAgents，默认 0 = 不限制（保留既有行为）。
    /// 判据来自会话状态的递归计数，不是模型传参。
    /// </summary>
    private async Task<ToolExecutionResult?> CheckConcurrencyLimitAsync(string? sessionId, CancellationToken ct)
    {
        var limit = ResolveMaxConcurrentSubAgents();
        if (limit <= 0)
            return null;

        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        var sessionState = _services.GetService<ISessionStateManager>();
        if (sessionState is null)
        {
            _logger.LogWarning(
                "[SubAgent] 并发上限守卫跳过：ISessionStateManager 未注册（session={Session}）",
                sessionId);
            return null;
        }

        int running;
        try
        {
            running = await sessionState.GetRunningSubAgentDescendantCountAsync(sessionId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SubAgent] 并发上限查询失败，放行（session={Session}）", sessionId);
            return null;
        }

        if (running < limit)
            return null;

        return Fail(
            $"子代理并发数已达上限：当前主 Agent 下所有层级存活子代理 {running} 个，" +
            $"配置 SubAgentDelegation:MaxConcurrentSubAgents={limit}。" +
            "请等现有子代理结束后再派发，或调整该配置。");
    }

    /// <summary>
    /// 读取并发上限。默认 0 = 不限制；正数表示上限。
    /// </summary>
    private int ResolveMaxConcurrentSubAgents()
    {
        var configuration = _services.GetService<IConfiguration>();
        var raw = configuration?["SubAgentDelegation:MaxConcurrentSubAgents"];
        return int.TryParse(raw, out var value) ? value : 0;
    }

    /// <summary>
    /// 需求 6：是否允许 Agent 为子代理选择模型。默认 true（保持既有行为）；
    /// false 时忽略模型传参，改用 SubAgentDelegation:DefaultModelRoute。
    /// </summary>
    private bool ResolveAllowModelSelection()
    {
        var configuration = _services.GetService<IConfiguration>();
        var raw = configuration?["SubAgentDelegation:AllowModelSelection"];
        return !bool.TryParse(raw, out var value) || value;
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
    /// 子代理默认预加载的工具。选这三个的理由：只读调查是子代理的绝大多数用法，
    /// 且它们正是 ToolExposurePlanner.CapabilityBundleMembers 里 read 包的成员，语义自洽。
    /// 不放进 CoreToolIds——那是所有 agent 共用的全局常量，改它会波及主代理与所有模板。
    /// </summary>
    private static readonly IReadOnlyList<string> DefaultSubAgentPreloadToolIds =
        ["file_read", "file_search", "search_grep"];

    /// <summary>
    /// 构造 SubAgentSpawnRequest，供池化路径（Create/Execute）使用。
    /// 复用与一次性路径相同的解析结果（模板、LLM 路由、能力策略等）。
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
            // 预置只读三件套：子代理新 session 已加载集为空，不预加载的话它们会落在 deferred，
            // 首轮必须先 search_tools 才能读文件（Claude/GPT 实测均如此，白付一轮）。
            PreloadToolIds = DefaultSubAgentPreloadToolIds,
            TaskPlanId = taskPlanning.TaskPlanId,
            TaskNodeId = taskPlanning.TaskNodeId,
            ParentTaskNodeId = taskPlanning.ParentTaskNodeId,
            DelegationDepth = (taskPlanning.DelegationDepth ?? 0) + 1,
            MaxDelegationDepth = taskPlanning.MaxDelegationDepth,
            RoleInPlan = args.PoolRole ?? taskPlanning.RoleInPlan,
            // 池化路径的默认人读名就是池名（它已经是一个人给的名字，且跨轮次稳定）；
            // 显式 name 优先于池名。
            DisplayName = args.Name ?? args.PoolName,
            AvatarKey = args.Avatar,
            Responsibility = args.Responsibility,
            AllowSubDelegation = taskPlanning.AllowSubDelegation == true,
            AllowAgentCreation = taskPlanning.AllowAgentCreation,
            AssignedObjective = taskPlanning.AssignedObjective,
            ExpectedOutputContract = taskPlanning.ExpectedOutputContract,
            ParentExecutionDeadlineUtc = context.ExecutionDeadlineUtc,
            InvocationId = GetStringProp(json, "invocation_id")
                        ?? GetStringProp(json, "invocationId"),
            OriginToolId = originToolId,
            ParentExecutionIdentity = context.ExecutionIdentity,
            UsageBudget = context.UsageBudget,
        };
    }

    private static TokenUsageDto? SumUsage(IReadOnlyList<SubAgentInvocationResult> results)
    {
        var usages = results.Select(result => result.Usage).Where(usage => usage is not null).ToArray();
        if (usages.Length == 0)
            return null;

        long prompt = 0;
        long completion = 0;
        long cacheHit = 0;
        foreach (var usage in usages)
        {
            prompt += Math.Max(0, usage!.PromptTokens ?? 0);
            completion += Math.Max(0, usage.CompletionTokens ?? 0);
            cacheHit += Math.Max(0, usage.PromptCacheHitTokens ?? 0);
        }

        var boundedPrompt = ClampToInt(prompt);
        var boundedCompletion = ClampToInt(completion);
        var boundedCacheHit = Math.Min(boundedPrompt, ClampToInt(cacheHit));
        return new TokenUsageDto
        {
            PromptTokens = boundedPrompt,
            CompletionTokens = boundedCompletion,
            TotalTokens = boundedPrompt > int.MaxValue - boundedCompletion
                ? int.MaxValue
                : boundedPrompt + boundedCompletion,
            PromptCacheHitTokens = boundedCacheHit,
            PromptCacheMissTokens = boundedPrompt - boundedCacheHit,
        };
    }

    private static int ClampToInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)Math.Max(0, value);


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

        // 解析允许的工具子集
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

                // ── none mode: zero tools, pure reasoning ──
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

        var descriptors = _services.GetService<IPuddingToolCatalogService>()?.ListTools(request.WorkspaceId)
            ?? _services.GetService<IPuddingToolRegistry>()?.ListDescriptors();
        if (descriptors is null)
            return basePolicy with
            {
                AllowShellExecution = false,
                AllowFileWrite = false,
                AllowNetworkAccess = false,
                RequiresGrantToolNames = [],
            };

        var lowToolIds = descriptors
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

        // 需求 6：关闭「允许 Agent 为子代理选模型」时，忽略模型传参（含 fallback），
        // 改用配置的默认路由。
        // 刻意不依赖「传 null 让它找默认」：ILlmResolver 对空路由是明确拒绝的 ——
        // 它的契约写着「不存在平台默认路由」，所以必须把目标路由显式抄出来。
        if (!ResolveAllowModelSelection())
        {
            var configured = _services.GetService<IConfiguration>()?["SubAgentDelegation:DefaultModelRoute"];
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new InvalidOperationException(
                    "已关闭 SubAgentDelegation:AllowModelSelection，但未配置 " +
                    "SubAgentDelegation:DefaultModelRoute，无法确定子代理使用哪个模型。");
            }

            modelId = configured.Trim();
        }

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

    /// <summary>
    /// 查被续跑子代理上一轮记录的模型标识，供「续跑继承原模型路由」使用。
    /// 返回 null 表示无法确定（历史行 model_id 为空 / 会话状态层未注册 / 查询异常），
    /// 由调用方决定如何告警 —— 本方法自身不抛异常，避免把「查不到」升级成「不能续跑」。
    /// </summary>
    private async Task<string?> TryFindResumedModelRouteAsync(
        string resumeSubSessionId,
        string parentSessionId,
        CancellationToken ct)
    {
        var sessionState = _services.GetService<ISessionStateManager>();
        if (sessionState is null || string.IsNullOrWhiteSpace(parentSessionId))
            return null;

        try
        {
            var agents = await sessionState.GetSubAgentsAsync(parentSessionId, ct);
            var original = agents.FirstOrDefault(a =>
                string.Equals(a.SubSessionId, resumeSubSessionId, StringComparison.Ordinal));
            var model = original?.ModelId?.Trim();
            return string.IsNullOrWhiteSpace(model) ? null : model;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[SubAgent] 续跑继承模型路由查询失败 sub={Sub} parent={Parent}",
                resumeSubSessionId,
                parentSessionId);
            return null;
        }
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

    // ══════════════════════════════════════════════════════
    // 池操作失败分类（S5d）
    // ══════════════════════════════════════════════════════
    //
    // 为什么必须分类：这些失败的**下一步动作完全不同**。「稍后重试」对未就绪是对的，
    // 对「池满」永远无效；「换个名字」对未命中是对的，对「归属不匹配」也是误导（换个名字仍可能不是自己的）。
    // 把所有池异常渲染成同一句「池操作失败」，模型只能反复重试同一个注定失败的动作。

    /// <summary>池失败的可区分类别；<see cref="PoolFailureKind.Unknown"/> 表示不属于已知业务失败。</summary>
    private enum PoolFailureKind
    {
        Unknown,
        NotReady,
        OwnershipMismatch,
        Busy,
        PoolFull,
    }

    // 两个稳定文案前缀。
    // 为什么不用异常类型区分「忙」与「池满」：两者在池实现里都是 InvalidOperationException——
    // 它们确实是不同错误（一个等，一个腾地方），但平台不该为此各造一个异常类型（本任务也不允许新建异常）。
    // 因此按抛出方的文案前缀判定；前缀被改文案后会退化为 Unknown，落到既有通用提示，
    // 不会错报成另一类别（失败方向是安全的）。
    private const string PoolBusyMessageMarker = "is currently busy";
    private const string PoolFullMessageMarker = "pool is full (capacity=";

    private static PoolFailureKind ClassifyPoolFailure(Exception ex) => ex switch
    {
        // 顺序有意义：PoolNotReadyException / PoolOwnershipMismatchException 都继承
        // InvalidOperationException，必须先按具体类型判定，再落到文案匹配。
        PoolNotReadyException => PoolFailureKind.NotReady,
        PoolOwnershipMismatchException => PoolFailureKind.OwnershipMismatch,
        InvalidOperationException { Message: var busyMessage }
            when busyMessage.Contains(PoolBusyMessageMarker, StringComparison.Ordinal) => PoolFailureKind.Busy,
        InvalidOperationException { Message: var fullMessage }
            when fullMessage.Contains(PoolFullMessageMarker, StringComparison.Ordinal) => PoolFailureKind.PoolFull,
        _ => PoolFailureKind.Unknown,
    };

    /// <summary>
    /// 把已知池失败渲染为可行动文本；非已知类别返回 null，由调用方保留既有通用文案。
    /// </summary>
    private static string? DescribePoolFailure(Exception ex, string? poolName)
    {
        var entryName = string.IsNullOrWhiteSpace(poolName) ? "(未指定)" : poolName!;
        return ClassifyPoolFailure(ex) switch
        {
            PoolFailureKind.NotReady => DescribePoolNotReady((PoolNotReadyException)ex),
            PoolFailureKind.OwnershipMismatch =>
                DescribePoolOwnershipMismatch((PoolOwnershipMismatchException)ex, entryName),
            PoolFailureKind.Busy =>
                $"? 子代理池条目 '{entryName}' 正忙：它正在执行上一次任务，不能并发接第二个任务。\n\n" +
                $"提示：等它这一轮结束后重试，或换用另一个池条目（用 pool_name=\"{entryName}\" pool_action=\"list\" " +
                "看状态列，Busy 即正在执行）。",
            PoolFailureKind.PoolFull => DescribePoolFull(ex, entryName),
            _ => null,
        };
    }

    /// <summary>
    /// 未就绪：池视图还没从持久化存储加载完。
    /// <para>
    /// <b>为什么必须强调「不是池为空」：</b>未就绪时内存里根本没有内容，若报成空池，
    /// 模型会认为子代理都消失了，进而去做「重建 / 清理」这类不可逆动作；
    /// 而事实是「还不知道」。同理，Failed 是终态，说成「稍后重试」会让人无限等待一个不会自愈的池。
    /// </para>
    /// </summary>
/// <summary>
    /// 取该子会话当前那次运行的归档 id，用于清理后的复核。
    /// <para>
    /// 必须在清理**之前**调用：<c>GetSubAgentsAsync</c> 把「最新 run 行」join 进快照，
    /// run 行一删，<c>RunId</c> 就退回 <c>SubSessionId</c> 这个占位值，而按它查归档恒为 null。
    /// 拿这个 null 当「已删」的证据，等于用「查不到」证明「删掉了」。
    /// </para>
    /// <para>
    /// 查不到快照时退回 <c>SubSessionId</c>：池条目已销毁、投影行又被清过的极端情况下，
    /// 退化行为是「按子会话 id 查一次归档」，比凭空断言「已清理」安全。
    /// </para>
    /// </summary>
    private static async Task<string> ResolveRunArchiveIdAsync(
        ISubAgentManager manager,
        string subSessionId,
        CancellationToken ct)
    {
        try
        {
            var status = await manager.GetStatusAsync(subSessionId, ct);
            if (!string.IsNullOrWhiteSpace(status?.RunId))
                return status.RunId;
        }
        catch (Exception)
        {
            // 查不到不是错误：定位键退化到 SubSessionId，复核仍能给出「存在 / 不存在」。
        }

        return subSessionId;
    }

    /// <summary>
    /// 探测运行归档是否仍然存在。<c>null</c> 表示**无法判定**（服务未注册或查询抛异常）。
    /// <para>
    /// 三态而不是布尔：把「查不到」折成 false，等价于把「仪器坏了」报成「结论变了」。
    /// </para>
    /// </summary>
    private static async Task<bool?> ProbeRunArchiveAsync(
        ISubAgentRunStore? runStore,
        string runId,
        CancellationToken ct)
    {
        if (runStore is null)
            return null;

        try
        {
            return await runStore.GetRunArchiveAsync(runId, ct) is not null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DescribePoolNotReady(PoolNotReadyException ex)
    {
        var (verdict, next) = ex.Readiness switch
        {
            PoolReadiness.Failed => (
                "恢复已失败",
                "这是终态：池不会自动重试，重复调用无效。请先按下面的底层原因排查持久化存储（数据库打不开 / schema 缺失等），修复后重启 Core。"),
            PoolReadiness.Loading => (
                "正在加载",
                "这是暂时状态：加载完成后即可正常使用，请稍后重试。"),
            _ => (
                "尚未开始加载",
                "这是暂时状态：首次访问会触发加载，请稍后重试。"),
        };

        var cause = ex.InnerException is { } inner
            ? $"\n底层原因：{inner.GetType().Name}: {inner.Message}"
            : string.Empty;

        return $"? 子代理池未就绪（readiness={ex.Readiness}，{verdict}）：池视图还没有从持久化存储加载完成，" +
               "此刻无法判断池里到底有哪些条目。\n" +
               $"处置：{next}{cause}\n" +
               "注意：本次结果只说明「还不知道」，不代表「池里确实没有条目」，也不代表子代理已经消失；" +
               "不要据此做删除或重建决定，需要日志时可查 Core 启动期与 SubAgentPool 的加载记录。";
    }

    /// <summary>
    /// 归属不匹配：条目属于另一个父会话/工作区（或没有归属）。
    /// <para>
    /// 必须与「忙」区分：忙是「等一会儿同一个操作就能成功」，归属不匹配是「永远不可能成功」，
    /// 只能改用自己创建的条目。合并两者会让模型对一个权限性拒绝无限重试。
    /// </para>
    /// </summary>
    private static string DescribePoolOwnershipMismatch(
        PoolOwnershipMismatchException ex, string entryName)
    {
        var (dimension, detail) = ex.ReasonCode switch
        {
            PoolOwnershipMismatchException.OwnerSessionReason => (
                "其他父会话",
                "该条目由另一个父会话创建，跨父会话驱动会污染该会话的执行投影"),
            PoolOwnershipMismatchException.OwnerWorkspaceReason => (
                "其他工作区",
                "该条目属于另一个工作区，跨工作区复用会把子代理写到错误的目录与权限面"),
            PoolOwnershipMismatchException.OrphanReason => (
                "无归属（历史遗留）",
                "该条目没有创建者记录（历史遗留或从未真正执行过），按 fail-closed 不允许任何会话接管"),
            _ => ("未知维度", "归属校验未通过"),
        };

        var owner = string.IsNullOrWhiteSpace(ex.OwnerSessionId) ? "(无)" : ex.OwnerSessionId!;
        var requested = string.IsNullOrWhiteSpace(ex.RequestedSessionId) ? "(无)" : ex.RequestedSessionId!;

        return $"? 池操作被拒绝：子代理 '{entryName}' 属于{dimension}（reason={ex.ReasonCode}）。\n" +
               $"{detail}；平台在创建 run 之前就拒绝了本次请求，没有产生失败 run，池计数也没被污染。\n" +
               $"归属：父会话 {owner}，本次请求：{requested}。\n" +
               $"提示：改用本会话自己创建的池条目名；或用 pool_name=\"{entryName}\" pool_action=\"list\" " +
               "查看可用条目（list 里的 SubSessionId 是完整值，可直接复制给 sub_agent_id）。";
    }

    /// <summary>池满：容量耗尽。<b>下一步是腾地方</b>，不是等待（等待不会释放容量）。</summary>
    private static string DescribePoolFull(Exception ex, string entryName)
    {
        var capacity = TryReadPoolCapacity(ex.Message);
        var capacityText = capacity is null ? "已达上限" : $"容量 {capacity} 已达上限";
        return $"? 子代理池已满：{capacityText}，无法再创建或自动预留 '{entryName}'。\n" +
               "提示：先用 pool_name=\"<不用的条目>\" pool_action=\"destroy\" 销毁不用的条目再重试，" +
               "或降低并发派发的数量。";
    }

    /// <summary>
    /// 条目名未命中：确认「池里没有这个条目」。
    /// <para>
    /// 与未就绪严格区分：未就绪是「不知道有没有」（应等待），未命中是「确认没有」（应改名字或重建）。
    /// 两者处置相反，文案不能合并。
    /// </para>
    /// </summary>
    private static string DescribePoolEntryMissing(string? poolName, string action)
        => $"? 子代理 '{poolName}' 未命中：池里没有这个条目名（可能从未创建、已销毁，或名字拼错）。\n" +
           $"提示：先用 pool_name=\"{poolName}\" pool_action=\"list\" 查看可用条目名，再重试 {action}。";

    /// <summary>从池满文案里取回容量数值，让报错给出可核对的数字而不是只有「已满」。</summary>
    private static string? TryReadPoolCapacity(string message)
    {
        var start = message.IndexOf(PoolFullMessageMarker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += PoolFullMessageMarker.Length;
        var end = start;
        while (end < message.Length && char.IsAsciiDigit(message[end]))
            end++;

        return end > start ? message[start..end] : null;
    }

    /// <summary>
    /// 派生剩余预算可执行性守卫：父级真实剩余不足以支撑一次可执行子运行时，
    /// 在委派边界显式拒绝（带数值可诊断），而不是派发一个首轮即死的子代理。
    /// 非派生预算（IsDerivedRemainder=false）不受影响；判据本体在
    /// SubAgentExecutionOptions.DescribeBudgetInfeasibility（与批量除法共用，禁止另建一套）。
    /// </summary>
    private string? DescribeParentBudgetInfeasibility(ToolExecutionContext context)
    {
        var options = _services.GetService<IRuntimeExecutionConfigService>()?.GetOptions().SubAgents
                      ?? new SubAgentExecutionOptions();
        return options.DescribeBudgetInfeasibility(context.UsageBudget);
    }

    private sealed record ResolvedChildLlmRoute(
        LlmInvocationProfile Profile,
        LlmConfig Config);

    /// <summary>
    /// 子代理工作目录的解析规则：显式值优先，否则继承父代理 cwd（两者都空则保持 null）。
    /// 抽成纯函数是为了能直接断言这条规则，而不是靠跑一次真子代理去观察。
    /// </summary>
    internal static string? ResolveWorkingDirectory(string? explicitValue, string? parentValue) =>
        string.IsNullOrWhiteSpace(explicitValue) ? parentValue : explicitValue;

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
                ["name"] = args.Name,
                ["avatar"] = args.Avatar,
                ["responsibility"] = args.Responsibility,
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

    [ToolParam("Optional model route. Prefer the full 'providerId/modelId' form (look it up with list_llm_providers); a bare modelId registered under multiple providers fails resolution.")]
    public string? Model { get; init; }

    [ToolParam("Optional fallback model route tried once when the requested model route fails to resolve (route-resolution failure only). Default 'deepseek/deepseek-flash'. Set equal to model to disable the fallback.")]
    public string? FallbackModel { get; init; }

    [ToolParam("复用父代理的上下文环境（Fork + 分支 + 注入到子代理上下文），默认 false")]
    public bool? ReuseParentContext { get; init; }

    [ToolParam("Existing sub-agent id to resume with preserved context and a fresh Pudding-managed budget. Do not combine with tasks or pool_name.")]
    public string? ResumeSubAgentId { get; init; }

    [ToolParam("Human-readable sub-agent name shown in the UI (2-16 chars recommended). Unique among ACTIVE sub-agents of the same parent session; a duplicate gets -2/-3 ... appended. Omit to auto-generate <role>-<n>.")]
    public string? Name { get; init; }

    [ToolParam("Optional preset avatar key (<=32 chars). Omit to let the UI derive a stable avatar from name + sub-agent id.")]
    public string? Avatar { get; init; }

    [ToolParam("One-line responsibility shown on the sub-agent card (<=200 chars). Omit to fall back to the first sentence of the task summary.")]
    public string? Responsibility { get; init; }

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
    /// 池操作: create(创建并执行), execute(执行任务,默认), destroy(销毁子代理), sleep(休眠),
    /// list(列出池状态), cleanup, cleanup-bulk, cancel(取消一个运行中的子代理)。
    /// cancel 不需要 pool_name，按 sub_agent_id 定位。
    /// </summary>
    [ToolParam("池操作: create, execute(默认), destroy, sleep, list, cleanup, cleanup-bulk, cancel(取消运行中的子代理，需 sub_agent_id), logs(查看子代理事件与工具调用尾部，需 sub_agent_id), steer(给运行中的子代理发一条消息，需 sub_agent_id 与 steer_message)")]
    public string? PoolAction { get; init; }

    /// <summary>logs 动作返回的尾部条数（事件与工具调用各自取最后 N 条）。</summary>
    [ToolParam("logs 动作返回的尾部条数（事件与工具调用各取最后 N 条），默认 20，范围 1~200。")]
    public int? LogsTail { get; init; }

    /// <summary>
    /// 子代理 id（形如 "&lt;parent&gt;-sub-&lt;hash&gt;"）。用于 pool_action=cancel / logs / steer；
    /// 也是 cleanup 的重试定位键——池条目已销毁、名字查不到时，用它指定要清哪条历史。
    /// 可从 query_sub_agents 或 pool_action=list 的结果获取。
    /// </summary>
    [ToolParam("子代理 id（cancel/logs/steer 必填；cleanup 可用作条目已销毁后的重试定位键）")]
    public string? SubAgentId { get; init; }

    /// <summary>取消原因（可选，仅用于 pool_action=cancel）。会写入运行归档与事件。</summary>
    [ToolParam("取消原因（可选，用于 pool_action=cancel）")]
    public string? CancelReason { get; init; }

    /// <summary>
    /// 要注入运行中子代理的消息正文（仅用于 pool_action=steer）。
    /// 消息在子代理<b>下一次 LLM 调用前</b>作为 User 消息注入，不打断当前轮。
    /// </summary>
    [ToolParam("要发给运行中子代理的消息正文（用于 pool_action=steer）；在它下一轮 LLM 调用前注入，不打断当前轮")]
    public string? SteerMessage { get; init; }

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
