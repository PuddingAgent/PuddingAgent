namespace PuddingPlatform.Services;

/// <summary>运行中子代理的实测进度。</summary>
public readonly record struct SubAgentRunProgress(int Rounds, int ToolCalls);

/// <summary>
/// 2026-11-14：从 run 目录的增量日志现算「轮次 / 工具调用次数」。
///
/// <para>
/// 背景：<c>run.json</c> 只在 <c>CompleteRunAsync</c> 写终态时才带 <c>TotalRounds</c> /
/// <c>TotalToolCalls</c>；运行中这两个字段是 null，<see cref="SubAgentDiagnosticsService"/>
/// 用 <c>?? 0</c> 回落，于是「正在跑 12 轮、10 次工具调用」的子代理对外报「0 轮 0 次」——
/// 观测面是瞎的，而这正是「主代理要能看实时状态」要解决的问题。
/// </para>
///
/// <para>
/// 为什么改读侧而不是每轮重写 run.json：逐轮重写会产生持续 I/O，并让读侧有机会读到半写文件；
/// 而 <c>events.jsonl</c> / <c>tools.jsonl</c> 本来就是逐条追加的，天生就是进度事实源。
/// 调用方只在 manifest 缺字段且状态为 running 时调用，终态运行仍只读 run.json，
/// 不破坏 <see cref="SubAgentDiagnosticsService"/>「默认不加载 events.jsonl」的性能约定。
/// </para>
/// </summary>
public static class SubAgentRunProgressReader
{
    private const string EventsFileName = "events.jsonl";
    private const string ToolsFileName = "tools.jsonl";

    /// <summary>轮次完成事件的类型名。序列化形式可能是紧凑或缩进，故按两个 token 分别判断。</summary>
    private const string EventTypeToken = "eventType";
    private const string RoundCompletedToken = "subagent.round.completed";

    /// <summary>
    /// 读取指定 run 目录的进度。目录或两份日志都不存在时返回 null（调用方据此回落到 0）。
    /// 单份日志读取失败只损失该项计数，不抛出——诊断功能不应因为日志滚动作废。
    /// </summary>
    public static SubAgentRunProgress? TryRead(string? runDirectory)
    {
        if (string.IsNullOrWhiteSpace(runDirectory) || !Directory.Exists(runDirectory))
            return null;

        var eventsPath = Path.Combine(runDirectory, EventsFileName);
        var toolsPath = Path.Combine(runDirectory, ToolsFileName);

        if (!File.Exists(eventsPath) && !File.Exists(toolsPath))
            return null;

        return new SubAgentRunProgress(
            CountLines(eventsPath, line =>
                line.Contains(EventTypeToken, StringComparison.Ordinal)
                && line.Contains(RoundCompletedToken, StringComparison.Ordinal)),
            CountLines(toolsPath, static line => !string.IsNullOrWhiteSpace(line)));
    }

    /// <summary>流式逐行计数：不把整份日志读进内存（单份 events.jsonl 实测可达数十 KB）。</summary>
    private static int CountLines(string path, Func<string, bool> predicate)
    {
        if (!File.Exists(path))
            return 0;

        var count = 0;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (predicate(line))
                    count++;
            }
        }
        catch (IOException)
        {
            // 日志正在被写或已被清理：返回已数到的部分，诊断不应因此失败。
        }

        return count;
    }
}
