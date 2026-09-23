using PuddingCode.Tools.Definitions;
using PuddingRuntime.Services.Skills;

namespace PuddingRuntime.Services.Tools;

/// <summary>
/// 工具侧展示投影（presentation）声明表：工具 id → 该工具自己声明的 <c>Present</c> 纯函数。
/// <para>
/// 为什么需要它：Core 的 <see cref="ToolDefinition.Present"/> 是工具自有投影的正式契约，
/// 但当前内置工具仍由 <c>[Tool]</c> 特性 + <c>PuddingToolBase</c> 描述（尚未迁移成 ToolDefinition 实例），
/// 因此 <c>Present</c> 以「工具类上的 static 方法」形式就近声明，再由本表按 id 接线到事件发射侧。
/// 表里**只允许登记真实声明**；未登记的工具按 Core 契约降级 Generic（<c>null</c> 表示无声明）。
/// </para>
/// <para>
/// 本刀（前端改进 #1）只登记 4 个工具：terminal_start / terminal_wait / file_patch / search_grep。
/// 其余工具保持「无声明 ⇒ Generic」，这是刻意的小评审面，不是遗漏。
/// </para>
/// </summary>
internal static class ToolPresentationCatalog
{
    private static readonly Dictionary<string, Func<ToolPresentationInput, ToolPresentationIntent?>> Presenters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["terminal_start"] = TerminalStartTool.Present,
            ["terminal_wait"] = TerminalWaitTool.Present,
            ["file_patch"] = FilePatchTool.Present,
            ["search_grep"] = SearchGrepTool.Present,
        };

    /// <summary>按工具 id 解析 Present 声明；无声明返回 null（⇒ Generic）。</summary>
    public static Func<ToolPresentationInput, ToolPresentationIntent?>? TryResolve(string toolId)
        => Presenters.GetValueOrDefault(toolId);
}
