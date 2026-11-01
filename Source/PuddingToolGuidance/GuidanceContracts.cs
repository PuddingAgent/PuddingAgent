namespace PuddingToolGuidance;

/// <summary>
/// 一次工具调用的**字面信息**。
/// <para>
/// 刻意只收「看得见、判得准」的字段：命令文本、shell 方言、成功与否。不收对话历史、不收会话状态
/// —— 依据越多，规则越容易在边界上猜错，而猜错的提示比不提示更有害。
/// </para>
/// </summary>
/// <param name="ToolId">工具标识，如 <c>shell</c>、<c>terminal_start</c>。</param>
/// <param name="ShellDialect">shell 方言：<c>cmd</c> / <c>powershell</c> / <c>pwsh</c> / <c>bash</c> / <c>wsl</c>；非 shell 类工具为 null。</param>
/// <param name="Command">命令文本（含参数）。非 shell 类工具为 null。</param>
/// <param name="Succeeded">调用是否成功。<b>用法错误经常「成功」返回空结果</b>，所以规则不得只看这一位。</param>
/// <param name="Error">失败信息；成功时为 null。</param>
/// <param name="ConsecutiveFailureCount">本次之后该调用的连续失败次数（成功为 0）；由调用方用
/// <see cref="ConsecutiveFailureTracker"/> 维护。默认 0 不影响已有规则。</param>
/// <param name="RepeatedCommandCount">本会话窗口内同一条命令（含本次）出现的次数；由调用方用
/// <see cref="CommandOccurrenceTracker"/> 维护。默认 0/1 不影响已有规则。</param>
public sealed record GuidanceInput(
    string ToolId,
    string? ShellDialect = null,
    string? Command = null,
    bool Succeeded = true,
    string? Error = null,
    int ConsecutiveFailureCount = 0,
    int RepeatedCommandCount = 0);

/// <summary>一条短提示。<paramref name="RuleId"/> 用于去重与配额，不展示给模型。</summary>
public sealed record GuidanceAdvice(string RuleId, string Message);

/// <summary>
/// 一条引导规则。实现必须是<b>纯函数</b>：相同输入恒定相同输出，无状态、无 IO。
/// </summary>
public interface IGuidanceRule
{
    /// <summary>稳定标识，用于去重/配额/命中统计。</summary>
    string Id { get; }

    /// <summary>命中返回提示，否则返回 null。不得抛异常（调用方在热路径上）。</summary>
    GuidanceAdvice? Evaluate(GuidanceInput input);
}
