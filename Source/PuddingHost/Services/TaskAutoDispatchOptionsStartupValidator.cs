using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PuddingCode.Goals;
using PuddingPlatform.Services.Scheduling;

namespace PuddingAgent.Services;

/// <summary>
/// TaskAutoDispatch 启动配置自愈（产品启动硬化）。
///
/// 背景：真实开发库存在历史配置 <c>TaskAutoDispatch.Enabled=true + Mode=authoritative</c>，
/// 但 <c>GoalRuns.Enabled=false</c>（旧版本遗留）。严格的 Options 校验会让 Core 直接启动失败，
/// 用户既无法进入页面也无法修复配置。
///
/// 策略：只对「authoritative 缺 Goal 前置」这一条降级为 <c>Mode=disabled</c> 并记录 Error，
/// 其余非法配置仍 fail-closed。降级后调度完全关闭（不会在 goals 关闭时偷跑任务），
/// 配置修复后需重启生效。
/// </summary>
public sealed class TaskAutoDispatchOptionsStartupValidator(
    IOptions<TaskBoundGoalOptions> taskBoundGoals,
    IOptions<GoalRunOptions> goalRuns,
    ILogger<TaskAutoDispatchOptionsStartupValidator> logger)
    : IValidateOptions<TaskAutoDispatchOptions>
{
    private const string GoalPrerequisitePrefix =
        "Authoritative TaskAutoDispatch requires";

    public ValidateOptionsResult Validate(string? name, TaskAutoDispatchOptions options)
    {
        var errors = TaskAutoDispatchOptions.Validate(
            options, taskBoundGoals.Value, goalRuns.Value);
        if (errors.Count == 0)
            return ValidateOptionsResult.Success;

        if (errors.Count == 1
            && errors[0].StartsWith(GoalPrerequisitePrefix, StringComparison.Ordinal))
        {
            logger.LogError(
                "[TaskAutoDispatch] 配置前置不一致：{Error}；启动自愈将 Mode 降级为 disabled（调度不运行，等待修复配置后重启）",
                errors[0]);
            options.Mode = "disabled";
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(errors);
    }
}
