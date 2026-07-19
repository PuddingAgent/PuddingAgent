using System.Diagnostics;
using System.Text.Json;
using PuddingCode.Agents;
using PuddingCode.Tools;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// Smart 工作流工具基类 — 提取公共子代理调用逻辑。
///
/// 设计原则：
///   · Agent = Function — 工具签名即自然语言
///   · 薄包装 — 核心逻辑在 role_in_plan 驱动的子代理中
///   · MainAgentOnly — 不暴露给子代理，防止循环
///   · 模型选择 — 通过 role_in_plan → manifest.{Role}Model 推导
/// </summary>
public abstract class SmartWorkflowToolBase<TArgs> : PuddingToolBase<TArgs> where TArgs : class, new()
{
    protected const string SubAgentTemplateId = "workspace-task-agent";

    protected abstract string RoleName { get; }
    protected abstract string BuildTaskPrompt(TArgs args, ToolExecutionContext context);
    protected abstract int DefaultTimeoutSeconds { get; }
    protected virtual int DefaultMaxRounds => 15;
    /// <summary>子代理允许的工具列表，逗号分隔。null = 继承父代理全部工具。</summary>
    protected virtual string? AllowedTools => null;

    protected async Task<ToolExecutionResult> RunSubAgentAsync(
        TArgs args,
        ToolExecutionContext context,
        IServiceProvider services,
        ILogger logger,
        CancellationToken ct,
        int? timeoutSeconds = null)
    {
        var task = BuildTaskPrompt(args, context);
        var timeout = timeoutSeconds ?? DefaultTimeoutSeconds;
        var toolName = GetType().Name;
        var workingDirectory = ResolveWorkingDirectory(args);

        // 从父 Agent manifest 解析角色对应的模型
        var model = await ResolveRoleModelAsync(context.AgentInstanceId, services, logger);

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
                allow_sub_delegation = false,
                tools = AllowedTools,
                reuse_parent_context = true,
                origin_tool_id = Descriptor.ToolId,
            });

            logger.LogInformation(
                "[{Tool}] agent={Agent} role={Role} spawning sub-agent timeout={Timeout}s",
                toolName, context.AgentInstanceId, RoleName, timeout);

            var toolExec = services.GetRequiredService<IPuddingToolExecutionService>();
            var result = await toolExec.ExecuteAsync("spawn_sub_agent", spawnArgs, context, null, ct);

            sw.Stop();

            if (result.Success)
            {
                logger.LogInformation(
                    "[{Tool}] agent={Agent} SUCCESS duration={Duration}ms output={OutputLen} chars",
                    toolName, context.AgentInstanceId, sw.ElapsedMilliseconds,
                    result.Output?.Length ?? 0);
                return ToolExecutionResult.Ok(result.Output);
            }

            var failMsg = $"❌ {toolName} sub-agent FAILED.\n   Error: {result.Error}\n   Role: {RoleName}\n   Duration: {sw.ElapsedMilliseconds}ms";
            logger.LogError("[{Tool}] agent={Agent} FAILED error={Error}", toolName, context.AgentInstanceId, result.Error);
            return ToolExecutionResult.Fail(failMsg);
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
}

/// <summary>需要文件、会话或研究边界的 Smart 工作流请求。</summary>
public abstract class ScopedSmartWorkflowArgs : SmartWorkflowArgs
{
    [ToolParam("任务范围，例如目录、文件、会话或研究边界")]
    public string? Scope { get; set; }
}
