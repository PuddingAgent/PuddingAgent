using PuddingAgent.Services;

namespace PuddingHost.Tests.Hosting;

public sealed class HeartbeatPromptComposerTests
{
    [Fact]
    public void AppendAutonomousExecutionContract_OverridesConsultativeInstancePrompt()
    {
        var result = HeartbeatPromptComposer.AppendAutonomousExecutionContract(
            "请先询问用户是否继续。  ");

        Assert.StartsWith("请先询问用户是否继续。", result, StringComparison.Ordinal);
        Assert.EndsWith(
            HeartbeatPromptComposer.AutonomousExecutionContract,
            result,
            StringComparison.Ordinal);
        Assert.Contains("心跳是自主执行轮次，不是咨询轮次", result, StringComparison.Ordinal);
        Assert.Contains("不得询问用户", result, StringComparison.Ordinal);
        Assert.Contains("query_session_logs(exclude_heartbeat=true)", result, StringComparison.Ordinal);
        Assert.Contains("spawn_sub_agent", result, StringComparison.Ordinal);
        Assert.Contains("不要以问题结束心跳回复", result, StringComparison.Ordinal);
    }
}
