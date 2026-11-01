using System.Text;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// 生成「续跑一轮」的模型提示。
/// <para>
/// 对齐 DeepSeek Harness `goal-round-driver/prompt.ts` 的做法：目标用 JSON 转义（防止目标文本里的
/// 引号/换行改写指令结构）、明确轮号与上限、并明说**不要把先前的叙述当成现状**。
/// </para>
/// <para>
/// 为什么这几句是必要的：模型在长会话里最先相信的就是自己上一轮的总结。若不明确否定它，
/// 续跑会变成"复述上一轮"而不是"继续干活" —— 这正是目标模式最典型的空转形态。
/// </para>
/// </summary>
public static class GoalRoundPrompt
{
    /// <summary>一轮提示的正文。纯函数，便于逐字断言。</summary>
    public static string Render(GoalState goal, int round)
    {
        ArgumentNullException.ThrowIfNull(goal);
        if (round <= 0)
            throw new ArgumentOutOfRangeException(nameof(round), "round must be positive.");

        var sb = new StringBuilder();
        sb.AppendLine("<goal_round>");
        sb.Append("Objective: ").AppendLine(JsonQuote(goal.Objective));
        sb.Append("Round: ").Append(round).Append('/').AppendLine(goal.MaxRounds.ToString());
        sb.AppendLine();
        sb.AppendLine("Continue working toward the objective in this same session. Treat the current workspace,");
        sb.AppendLine("tool results, and durable state as authoritative; inspect them instead of assuming earlier");
        sb.AppendLine("narration is still current. Make concrete progress and verify it. Before claiming completion,");
        sb.AppendLine("gather evidence that the whole objective is achieved, then mark the goal complete with that");
        sb.AppendLine("evidence. If work remains, leave the goal active for the next round.");
        sb.AppendLine("</goal_round>");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 最小的 JSON 字符串转义：只处理会破坏指令结构的字符（引号、反斜杠、换行、制表、尖括号）。
    /// 不引入序列化器是为了让这段输出逐字可测、无版本差异。
    /// </summary>
    public static string JsonQuote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // 尖括号也必须转义：目标文本里若出现 </goal_round> 之类的标签字面量，
                // 会与指令结构的闭合标签混淆（实测过：一条刁钻目标能让闭合标签出现两次）。
                case '<': sb.Append("\\u003C"); break;
                case '>': sb.Append("\\u003E"); break;
                default: sb.Append(ch); break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }
}
