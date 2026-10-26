namespace PuddingBrowser.Abstractions;

/// <summary>
/// **pageVersion 的递增判据（单一真源）**。
///
/// <para>
/// pageVersion 是「之前观测到的元素引用是否还有效」的**唯一**判据：任何一次整块替换 DOM 的导航都必须让它递增，
/// 否则旧 ref 会在新页面上被判为仍然有效——调用方以为在点"搜索框"，实际点到了完全不同的元素。
/// </para>
///
/// <para>
/// 为什么需要一条独立判据：**同文档导航**（SPA 的 <c>history.pushState/replaceState</c>）会换掉整页内容，
/// 却**不触发** <c>NavigationCompleted</c>；而真实导航既触发 <c>SourceChanged</c> 又触发 <c>NavigationCompleted</c>。
/// 若只在 <c>NavigationCompleted</c> 里递增，SPA 路由切换后版本号纹丝不动。
/// </para>
///
/// <para>
/// **实测证据（2026-10-26）**：抖音推荐流里按坐标点左下角「我的」，URL 从
/// <c>https://www.douyin.com/?recommend=1</c> 变成
/// <c>https://www.douyin.com/user/self?from_tab_name=main&amp;showTab=favorite_collection</c>、
/// 标题也从「抖音-记录美好生活」变成「闲小鱼的抖音 - 抖音」，但 <c>browser_tabs list</c> 回报的
/// <c>pageVersion</c> 仍是 <c>2</c>。此前观测到的 ref 因此被误判为仍然有效。
/// </para>
/// </summary>
public static class PageVersionPolicy
{
    /// <summary>
    /// 收到 <c>SourceChanged</c> 之后，是否需要递增 pageVersion。
    /// </summary>
    /// <param name="previousUrl">事件发生前记录的 URL。</param>
    /// <param name="newUrl">事件带回的 URL（<c>null</c>/空表示这一面还没拿到地址）。</param>
    /// <param name="navigationInFlight">
    /// 是否有真实导航正在进行（<c>NavigationStarting</c> 之后、<c>NavigationCompleted</c> 之前）。
    /// 真实导航由 <c>NavigationCompleted</c> 负责递增（那里还能区分成功/失败），这里不重复计。
    /// </param>
    /// <returns>为真表示这是一次**没有导航、但文档已经换了**的同文档跳转，必须递增。</returns>
    public static bool BumpsOnSourceChange(string? previousUrl, string? newUrl, bool navigationInFlight)
    {
        // 真实导航：交给 NavigationCompleted（它还能拿到 IsSuccess）。
        if (navigationInFlight)
        {
            return false;
        }

        // 拿不到新地址就不猜：没有证据说明文档换了，保持版本不动。
        if (string.IsNullOrEmpty(newUrl))
        {
            return false;
        }

        // 逐字节比较：查询串/锚点变化同样意味着这一页的内容可能整块换了，
        // 宁可为一次无关紧要的变化多花一次重新观测，也不能让旧引用悄悄继续有效。
        return !string.Equals(previousUrl, newUrl, StringComparison.Ordinal);
    }
}
