using System.Text;
using Microsoft.Extensions.Logging;

namespace PuddingRuntime.Services;

/// <summary>
/// Agent 压缩感知通知器。
///
/// 在系统压缩前，通知当前 Agent 生成工作纪要。
/// Agent 的工作总结会被纳入 Flash 摘要生成器的输入，
/// 确保关键信息（服务器地址、项目目录、待办事项等）被保留。
///
/// 流程：
/// 1. ContextWindowManager 检测到阈值 → 调用 RequestAgentWorkSummaryAsync()
/// 2. Agent 看到系统指令 → 生成工作纪要
/// 3. Agent 的响应被收集 → 传给 ContextCompactionService.CompactAsync()
/// 4. Flash LLM 将 Agent 的总结纳入压缩摘要
/// 5. 压缩摘要保存到 SessionSummaryStore → 新 Session 可召回
/// </summary>
public sealed class AgentCompactionNotifier
{
    private readonly ILogger<AgentCompactionNotifier> _logger;

    /// <summary>
    /// 工作纪要的系统提示词。
    /// 注入到 Agent 的消息历史中，触发 Agent 生成工作总结。
    /// </summary>
    private const string WorkSummaryPrompt = """
【系统指令：会话压缩即将触发】

当前会话即将进行上下文压缩。请立即生成一份工作纪要，包含以下内容：

1. **当前工作目标**：你正在帮用户做什么
2. **已完成的工作**：本次会话完成了哪些任务
3. **关键信息记录**：
   - 涉及的服务器地址、项目目录、文件路径
   - 重要的配置参数、端口号、环境变量
   - 需要记住的代码位置、函数名、类名
4. **未完成的工作**：还有哪些任务待处理，进行到哪一步了
5. **用户偏好和约束**：用户明确表达的偏好或要求
6. **下一步建议**：建议接下来做什么

请用清晰的 Markdown 格式输出，保持简洁但完整。
这份纪要会在下个 Session 中被召回，帮助你继续工作。
""";

    public AgentCompactionNotifier(ILogger<AgentCompactionNotifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 获取工作纪要提示词。
    /// </summary>
    public string GetWorkSummaryPrompt() => WorkSummaryPrompt;

    /// <summary>
    /// 检查消息内容是否是 Agent 的工作纪要响应。
    /// 通过检查是否包含工作纪要的特征结构来判断。
    /// </summary>
    public bool IsAgentWorkSummaryResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        // 检查是否包含工作纪要的典型结构
        var indicators = new[]
        {
            "当前工作目标",
            "已完成的工作",
            "已完成事项",
            "关键信息",
            "未完成的工作",
            "待处理",
            "下一步建议",
            "下一步"
        };

        var matchCount = indicators.Count(indicator =>
            content.Contains(indicator, StringComparison.OrdinalIgnoreCase));

        // 至少匹配 3 个指标才认为是工作纪要
        return matchCount >= 3;
    }

    /// <summary>
    /// 构建带 Agent 工作总结的 SessionSummaryStore 内容。
    /// 将 Agent 的总结标记为优先参考内容。
    /// </summary>
    public string BuildTaggedSummary(string agentWorkSummary)
    {
        return $"""
> 🤖 **Agent 主动工作总结**（压缩前自动生成）

{agentWorkSummary}
""";
    }

    /// <summary>
    /// 构建合并了 Agent 总结和系统摘要的最终内容。
    /// 用于保存到 SessionSummaryStore。
    /// </summary>
    public string BuildMergedSummary(string systemSummary, string? agentWorkSummary)
    {
        if (string.IsNullOrWhiteSpace(agentWorkSummary))
            return systemSummary;

        var sb = new StringBuilder();

        // Agent 总结放在前面，确保优先被注意到
        sb.AppendLine("## 🤖 Agent 主动工作总结（压缩前生成）");
        sb.AppendLine();
        sb.AppendLine(agentWorkSummary);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 📋 系统压缩摘要");
        sb.AppendLine();
        sb.AppendLine(systemSummary);

        return sb.ToString();
    }
}
