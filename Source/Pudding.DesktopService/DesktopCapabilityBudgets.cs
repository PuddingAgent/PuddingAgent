using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// 能力预算的**唯一实现**：把 Core 请求里的上限落到 Desktop 侧的结果上。
///
/// 为什么必须集中：每个 <see cref="Pudding.Contracts.Desktop.IDesktopUiSurface"/> 实现（探针、WinUI）
/// 都要遵守「按预算截断且如实标注」这条不变量。分散实现的结果是「有的实现忘了截断」——
/// 本系列就出现过探针执行器不执行预算的情况。集中成纯函数后，实现方只需调用一次。
///
/// 语义（诚实优先）：
/// · 只要丢弃了任何内容 ⇒ <c>Truncated = true</c>，**不**假装完整；
/// · 调用方原本已标注 <c>Truncated</c> 的，保持为真（不因本次未再截断而清零）；
/// · 不抛异常：预算是上限而不是硬错误，超限即截断。
/// </summary>
public static class DesktopCapabilityBudgets
{
    /// <summary>按请求预算截断快照文本字段。</summary>
    public static DesktopSnapshot Apply(DesktopSnapshot snapshot, DesktopSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);

        var domText = Truncate(snapshot.DomText, options.IncludeDom ? options.MaxTextLength : 0);
        var accessibilityTree = Truncate(
            snapshot.AccessibilityTree, options.IncludeAccessibilityTree ? options.MaxTextLength : 0);
        var html = Truncate(snapshot.Html, options.IncludeHtml ? options.MaxTextLength : 0);

        var dropped = domText.Dropped || accessibilityTree.Dropped || html.Dropped;

        return new DesktopSnapshot(
            snapshot.Target,
            domText.Value,
            accessibilityTree.Value,
            html.Value,
            snapshot.Truncated || dropped,
            snapshot.NodeCount,
            snapshot.PageVersion);
    }

    /// <summary>按请求上限截断脚本结果（字节级：截断后可能不再是合法 JSON，故必须同时标注 Truncated）。</summary>
    public static JavascriptResult Apply(JavascriptResult result, int maxResultBytes)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (maxResultBytes <= 0 || result.JsonValue is null)
        {
            return result;
        }

        var value = result.JsonValue;
        var dropped = false;
        if (value.Length > maxResultBytes)
        {
            value = value[..maxResultBytes];
            dropped = true;
        }

        return new JavascriptResult(result.Kind, value, result.Truncated || dropped);
    }

    /// <summary>按请求上限截断定位结果（取前 N 个命中项）。</summary>
    public static DesktopLocateResult Apply(DesktopLocateResult result, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (maxResults <= 0 || result.Elements.Count <= maxResults)
        {
            return result;
        }

        return new DesktopLocateResult(
            result.Target,
            result.Locator,
            result.Elements.Take(maxResults).ToArray(),
            truncated: true,
            result.PageVersion);
    }

    /// <summary>按请求上限截断页面清单（上下文内页面数）。</summary>
    public static DesktopContexts Apply(DesktopContexts contexts, int maxPagesPerContext)
    {
        ArgumentNullException.ThrowIfNull(contexts);

        if (maxPagesPerContext <= 0)
        {
            return contexts;
        }

        var truncated = false;
        var adjusted = new List<DesktopContextInfo>(contexts.Contexts.Count);

        foreach (var context in contexts.Contexts)
        {
            if (context.Pages.Count <= maxPagesPerContext)
            {
                adjusted.Add(context);
                continue;
            }

            truncated = true;
            adjusted.Add(new DesktopContextInfo(
                context.ContextId, context.Trust, context.Pages.Take(maxPagesPerContext).ToArray()));
        }

        return truncated ? new DesktopContexts(adjusted, contexts.ObservedVersion) : contexts;
    }

    /// <summary>
    /// 按预算收敛剪贴板内容。剪贴板可能含极长文本甚至凭据 ⇒ **绝不无界回传**；
    /// 同时再夹一层硬上限（即使调用方算了假预算，也不会超过 <see cref="ClipboardReadRequest.MaxMaxCharacters"/>）。
    /// </summary>
    public static DesktopClipboardContent Apply(DesktopClipboardContent content, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(content);

        var effective = Math.Clamp(maxCharacters, 1, ClipboardReadRequest.MaxMaxCharacters);
        var text = content.Text;

        if (text is null || text.Length <= effective)
        {
            return content;
        }

        return new DesktopClipboardContent(text[..effective], truncated: true);
    }
    private static (string? Value, bool Dropped) Truncate(string? value, int limit)
    {
        if (value is null || limit <= 0 || value.Length <= limit)
        {
            return (limit <= 0 ? null : value, limit <= 0 && value is not null);
        }

        return (value[..limit], true);
    }
}
