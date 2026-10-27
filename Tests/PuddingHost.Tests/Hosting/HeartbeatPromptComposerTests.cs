using System.Text;
using PuddingAgent.Services;

namespace PuddingHost.Tests.Hosting;

public sealed class HeartbeatPromptComposerTests
{
    [Fact]
    public void AppendAutonomousExecutionContract_AppendsContractAfterConsultativePrompt()
    {
        var result = HeartbeatPromptComposer.AppendAutonomousExecutionContract(
            "  请先询问用户是否继续。  ");

        Assert.StartsWith("请先询问用户是否继续。", result, StringComparison.Ordinal);
        Assert.EndsWith(
            HeartbeatPromptComposer.AutonomousExecutionContract,
            result,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 契约必须覆盖每一条自主执行规则。
    /// 锚点只取工具名、参数名与指令关键词，不取散文措辞——措辞可以精简
    /// （967da97 曾把契约从 518 压到 418 字符），但标识符一旦缺失就代表规则真的丢了。
    /// </summary>
    [Theory]
    [InlineData("不得询问用户")]                                 // 不是咨询轮次
    [InlineData("goal_read")]                                    // 先读目标
    [InlineData("query_session_logs(exclude_heartbeat=true)")]   // 恢复已授权未完成工作
    [InlineData("spawn_sub_agent")]                              // 并行/编码委派
    [InlineData("query_sub_agents")]                             // 子代理结果验收
    [InlineData("不得扩大授权范围")]                              // 授权边界
    [InlineData("sleep")]                                        // 收口时机
    [InlineData("send_message")]                                 // 飞书汇报
    [InlineData("work_summary")]                                 // 汇报失败兜底
    [InlineData("不以问题结束")]                                  // 禁止反问收尾
    public void AutonomousExecutionContract_KeepsRequiredRuleAnchors(string anchor)
    {
        Assert.Contains(
            anchor,
            HeartbeatPromptComposer.AutonomousExecutionContract,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// 契约每次心跳都会注入，体积必须有硬上限，否则文案会随修订逐次回涨而无人察觉。
    /// 触发本断言时：先确认新加的规则确有必要，再上调预算并说明理由。
    /// </summary>
    [Fact]
    public void AutonomousExecutionContract_StaysWithinInjectionBudget()
    {
        const int budgetBytes = 900;
        var bytes = Encoding.UTF8.GetByteCount(HeartbeatPromptComposer.AutonomousExecutionContract);

        Assert.True(
            bytes <= budgetBytes,
            $"心跳契约体积 {bytes} 字节，超出 {budgetBytes} 字节预算。它每次心跳都会注入，请压缩后再提交。");
    }
}
