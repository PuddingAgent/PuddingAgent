using System.Text.Json;
using PuddingRuntime.Services.Skills;
using PuddingToolGuidance;

namespace PuddingRuntime.Services.AgentLoop;

/// <summary>
/// Harness 层「工具用法引导」插件：在工具结果末尾追加一条短提示（最多 3 条）。
/// <para>
/// 它只实现 <see cref="IAgentLoopHook.OnToolResultTransformingAsync"/> —— 不碰 Core、不碰任何工具的
/// <c>ExecuteAsync</c>。新增一类检查 = 往 <c>GuidanceEngine</c> 加一条规则，或再注册一个 Hook。
/// </para>
/// <para>
/// 五条硬约束：
/// ① 只追加文本，绝不改 <see cref="SkillResult.Success"/> / <see cref="SkillResult.ExitCode"/>；
/// ② 参数解析失败、规则抛异常一律原样返回 —— 引导层不能把一次正常调用变成故障；
/// ③ 命令类规则只对 shell 工具求值（其它工具短路，省掉解析）；循环检测对所有工具生效；
/// ④ 默认方言按工具分流（<c>shell=auto</c> 在 Windows 上是 PowerShell，<c>terminal_*</c> 是 cmd）；
/// ⑤ **宁可漏提示，不可乱提示** —— 判不出来就返回 null，不猜。
/// </para>
/// </summary>
public sealed class ToolGuidanceHook : IAgentLoopHook
{
    /// <summary>会产生 shell 命令字面量的工具。</summary>
    private static readonly HashSet<string> ShellToolIds =
        new(StringComparer.OrdinalIgnoreCase) { "shell", "terminal_start", "terminal_execute" };

    private readonly GuidanceEngine _engine;
    private readonly ConsecutiveFailureTracker _failures;
    private readonly CommandOccurrenceTracker _occurrences;

    public ToolGuidanceHook(
        GuidanceEngine? engine = null,
        ConsecutiveFailureTracker? failures = null,
        CommandOccurrenceTracker? occurrences = null)
    {
        _engine = engine ?? new GuidanceEngine();
        _failures = failures ?? new ConsecutiveFailureTracker();
        _occurrences = occurrences ?? new CommandOccurrenceTracker();
    }

    public Task<SkillResult> OnToolResultTransformingAsync(
        AgentLoopContext context,
        int round,
        string toolName,
        string argsJson,
        SkillResult result,
        CancellationToken ct = default)
    {
        if (result is null)
            return Task.FromResult(result!);

        // 循环检测对所有工具生效：单次失败正常，连续同样的失败才是信号。
        var failureKey = context.SessionId + "|" + toolName;
        var consecutiveFailures = _failures.Register(failureKey, result.Success);

        string? dialect = null;
        string? command = null;
        if (ShellToolIds.Contains(toolName))
            (dialect, command) = Resolve(toolName, argsJson);

        // 「同一条命令又跑了一遍」：用于提示跳过构建这类重复开销（成功不清零，与失败计数不同）。
        var repeatedCommandCount = string.IsNullOrWhiteSpace(command)
            ? 0
            : _occurrences.Register(context.SessionId + "|" + command.Trim());

        var summary = _engine.Evaluate(new GuidanceInput(
            ToolId: toolName,
            ShellDialect: dialect,
            Command: command,
            Succeeded: result.Success,
            Error: result.Error,
            ConsecutiveFailureCount: consecutiveFailures,
            RepeatedCommandCount: repeatedCommandCount,
            Output: result.Output));

        if (!summary.HasAdvice)
            return Task.FromResult(result);

        // 用 `with` 复制：保留 Success/ExitCode/Metadata/ContentParts，只换 Output。
        return Task.FromResult(result with { Output = summary.AppendTo(result.Output) });
    }

    /// <summary>
    /// 从参数里取出「方言 + 命令文本」。取不到就给空，命令类规则自然不命中。
    /// <para>
    /// 默认方言<b>按工具分别取</b>，不能统一：<c>shell</c> 工具的 <c>shell=auto</c> 在 Windows 上是
    /// PowerShell，而 <c>terminal_*</c> 工具默认就是 cmd.exe。默认值搞反会让规则**系统性误报**，
    /// 那比不提示更糟。
    /// </para>
    /// </summary>
    internal static (string? Dialect, string? Command) Resolve(string toolName, string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return (DefaultDialect(toolName), null);

        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (DefaultDialect(toolName), null);

            var root = doc.RootElement;
            var command = ReadString(root, "command");
            var declared = ReadString(root, "shell");

            var dialect = string.IsNullOrWhiteSpace(declared)
                ? DefaultDialect(toolName)
                : declared.Trim().ToLowerInvariant();

            // shell=auto 等价于「当前平台的默认 shell」：Windows 上是 PowerShell。
            if (dialect is "auto" or "")
                dialect = DefaultDialect(toolName);

            return (dialect, command);
        }
        catch (JsonException)
        {
            // 参数不是合法 JSON：不提示，也别把异常抛进主执行链。
            return (null, null);
        }
    }

    private static string? DefaultDialect(string toolName) =>
        toolName.Equals("shell", StringComparison.OrdinalIgnoreCase) ? "powershell" : "cmd";

    private static string? ReadString(JsonElement root, string propertyName)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
        }

        return null;
    }
}
