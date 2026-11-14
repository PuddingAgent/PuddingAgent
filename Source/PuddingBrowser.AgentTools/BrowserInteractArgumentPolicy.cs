namespace PuddingBrowser.AgentTools;

/// <summary>
/// 指针动作的参数形状判定（纯函数，无 IO，可直接单测）。
///
/// 为什么单独成一处：<c>pointer_click</c> / <c>pointer_move</c> 默认按坐标表达
/// （契约层禁止 locator 与坐标混用），但**坐标动作没有元素身份**——判定层看不到它在
/// 点谁，滚出视口、被遮挡、已被禁用都拦不住，只能靠调用方自己保证坐标有效。
/// 一旦调用方给出 locator，语义就变成"点这个元素"，这里把它翻成 <c>click</c> / <c>hover</c>，
/// 让后续走元素路径，复用已有的可见性/视口/遮挡/禁用判定与派发前坐标重采样。
///
/// 规则集中在一个可单测的函数里，而不是埋在 lambda 里只靠运行时回执验证——
/// 这类"改写成另一个动作"的分支一旦错了，回执仍会报成功，是最难发现的那种错。
/// </summary>
internal static class BrowserInteractArgumentPolicy
{
    /// <summary>
    /// 解析最终的 action。<c>Error</c> 非空表示参数形状不合法，调用方应直接失败，
    /// 此时返回的 <c>Action</c> 不可使用。
    /// </summary>
    internal static (string Action, string? Error) ResolvePointer(
        string action, bool hasLocator, bool hasPoint, bool hasDragTarget)
    {
        if (action is not ("pointer_click" or "pointer_move" or "pointer_drag"))
        {
            return (action, null);
        }

        // 点/悬停：locator 与坐标是两种表达，给一个就够；两个都给意味着调用方自己也没想清楚
        // 要按哪个执行——静默取其一都会产生"看起来成功但点错了地方"。
        if (hasLocator && action is not "pointer_drag")
        {
            if (hasPoint)
            {
                return (action, "give either locator or x/y for pointer_click/pointer_move, not both");
            }

            return (action == "pointer_click" ? "click" : "hover", null);
        }

        if (!hasPoint)
        {
            return (action, "pointer actions require both x and y (CSS viewport pixels)");
        }

        if (action == "pointer_drag" && !hasDragTarget)
        {
            return (action, "action=pointer_drag requires both to_x and to_y (CSS viewport pixels)");
        }

        // 拖拽有两个端点，单个 locator 无法表达"拖到哪儿"；元素间拖拽用 action=drag。
        if (hasLocator)
        {
            return (action,
                "pointer_drag takes coordinates; use action=drag with locator+to_locator to drag between elements");
        }

        return (action, null);
    }
}
