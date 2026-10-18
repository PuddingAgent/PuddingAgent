using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PuddingAgent.Services;
using PuddingCode.Goals;
using PuddingPlatform.Services.Scheduling;

namespace PuddingHost.Tests.Services;

/// <summary>
/// 产品启动硬化：仅对「authoritative 缺 Goal 前置」自愈降级，其余非法配置仍 fail-closed。
/// </summary>
public sealed class TaskAutoDispatchOptionsStartupValidatorTests
{
    [Fact]
    public void AuthoritativeWithoutGoalPrerequisites_DowngradesToDisabled()
    {
        var options = new TaskAutoDispatchOptions
        {
            Enabled = true,
            Mode = "authoritative",
        };
        var validator = Create(taskBoundGoalsEnabled: false, goalRunsEnabled: false);

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
        Assert.Equal("disabled", options.Mode);
    }

    [Fact]
    public void ValidAuthoritativeWithGoalPrerequisites_RemainsAuthoritative()
    {
        var options = new TaskAutoDispatchOptions
        {
            Enabled = true,
            Mode = "authoritative",
        };
        var validator = Create(taskBoundGoalsEnabled: true, goalRunsEnabled: true);

        var result = validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
        Assert.Equal("authoritative", options.Mode);
    }

    [Fact]
    public void OtherInvalidConfiguration_StillFails()
    {
        var options = new TaskAutoDispatchOptions
        {
            Enabled = true,
            Mode = "authoritative",
            ScanInterval = TimeSpan.Zero,
        };
        var validator = Create(taskBoundGoalsEnabled: true, goalRunsEnabled: true);

        var result = validator.Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("ScanInterval"));
    }

    private static TaskAutoDispatchOptionsStartupValidator Create(
        bool taskBoundGoalsEnabled,
        bool goalRunsEnabled)
        => new(
            Options.Create(new TaskBoundGoalOptions { Enabled = taskBoundGoalsEnabled }),
            Options.Create(new GoalRunOptions
            {
                Enabled = goalRunsEnabled,
                ContinuationEnabled = goalRunsEnabled,
            }),
            NullLogger<TaskAutoDispatchOptionsStartupValidator>.Instance);
}
