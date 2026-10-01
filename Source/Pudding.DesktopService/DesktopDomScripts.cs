using System.Globalization;
using System.Text;
using System.Text.Json;
using Pudding.Contracts.Desktop;

namespace Pudding.DesktopService;

/// <summary>
/// DOM 观察脚本的**生成与解析**：WinUI/WebView2 表面里唯一「有逻辑」的部分，
/// 与 WebView2 类型无关，因此可以脱离 UI 环境测试。
///
/// 设计约定（Desktop 内部约定，Core 永远看不到这段 JSON）：
/// · 脚本由本类生成，结果由本类解析——两边一起改，避免脚本与解析器漂移；
/// · 解析 **fail closed**：结构不符/字段缺失/类型不对一律返回 <c>null</c> 或错误，
///   由调用方折成 <c>internal_error</c>（不把半截数据当有效观测）；
/// · 引用（ref）由脚本在**本次调用内**分配，调用方必须把当前 PageVersion 一并带回来，
///   这样「Ref 随版本失效」在 Core 侧才成立。
/// </summary>
public static class DesktopDomScripts
{
    /// <summary>快照脚本：返回 { nodeCount, truncated, domText, accessibilityTree }。</summary>
    public static string BuildSnapshotScript(DesktopSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var includeDom = options.IncludeDom ? "true" : "false";
        var includeA11y = options.IncludeAccessibilityTree ? "true" : "false";

        return $$"""
            (() => {
              const maxNodes = {{options.MaxNodes}};
              const maxText = {{options.MaxTextLength}};
              let nodeCount = 0;
              let truncated = false;
              const lines = [];
              const walk = (element, depth) => {
                if (!element || nodeCount >= maxNodes) { truncated = nodeCount >= maxNodes; return; }
                nodeCount++;
                const tag = element.tagName ? element.tagName.toLowerCase() : '#text';
                const text = (element.textContent || '').replace(/\s+/g, ' ').trim();
                lines.push('  '.repeat(depth) + tag + (text ? ': ' + text.slice(0, 120) : ''));
                for (const child of element.children || []) { walk(child, depth + 1); }
              };
              walk(document.body, 0);
              let domText = {{includeDom}} ? lines.join('\n') : null;
              if (domText && domText.length > maxText) { domText = domText.slice(0, maxText); truncated = true; }
              const a11y = {{includeA11y}}
                ? Array.from(document.querySelectorAll('[role],[aria-label],h1,h2,h3,button,a,input,select,textarea'))
                    .slice(0, maxNodes)
                    .map(el => (el.getAttribute('role') || el.tagName.toLowerCase()) + ':' + (el.getAttribute('aria-label') || (el.textContent || '').trim().slice(0, 80)))
                    .join('\n')
                : null;
              return JSON.stringify({ nodeCount, truncated, domText, accessibilityTree: a11y });
            })()
            """;
    }

    /// <summary>定位脚本：返回 { truncated, elements: [{ ref, tag, role, name, text, visible, enabled, checked }] }。</summary>
    public static string BuildLocateScript(DesktopLocator locator, int maxResults)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var selector = SelectorFor(locator);

        return $$"""
            (() => {
              const selector = {{JsonString(selector)}};
              const max = {{maxResults}};
              const candidates = Array.from(document.querySelectorAll(selector));
              const truncated = candidates.length > max;
              const elements = candidates.slice(0, max).map((el, index) => {
                const rect = el.getBoundingClientRect();
                return {
                  ref: 'e' + (index + 1),
                  tag: el.tagName.toLowerCase(),
                  role: el.getAttribute('role'),
                  name: el.getAttribute('aria-label') || el.getAttribute('name'),
                  text: (el.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 200),
                  visible: rect.width > 0 && rect.height > 0,
                  enabled: !el.disabled,
                  checked: typeof el.checked === 'boolean' ? el.checked : null
                };
              });
              return JSON.stringify({ truncated, elements });
            })()
            """;
    }

    /// <summary>把定位描述符翻成 CSS 选择器（v1：不支持的策略由调用方在准入阶段拒绝）。</summary>
    public static string SelectorFor(DesktopLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        return locator.Kind switch
        {
            DesktopLocatorKind.Css => locator.Value,
            DesktopLocatorKind.XPath => locator.Value,
            DesktopLocatorKind.TestId => $"[data-testid={JsonString(locator.Value)}]",
            DesktopLocatorKind.Label => $"[aria-label={JsonString(locator.Value)}]",
            DesktopLocatorKind.Placeholder => $"[placeholder={JsonString(locator.Value)}]",
            DesktopLocatorKind.AltText => $"[alt={JsonString(locator.Value)}]",
            DesktopLocatorKind.Title => $"[title={JsonString(locator.Value)}]",
            DesktopLocatorKind.Role => $"[role={JsonString(locator.Value)}]",
            DesktopLocatorKind.Text => $"*:not(script):not(style)",
            // Ref 定位依赖上一次脚本分配的顺序（e1/e2/...）：同一 PageVersion 内稳定。
            DesktopLocatorKind.Ref => "*",
            _ => throw new ArgumentOutOfRangeException(nameof(locator), locator.Kind, "Locator kind is not supported."),
        };
    }

    /// <summary>解析快照结果；结构不符返回 <c>null</c>（fail closed）。</summary>
    public static DesktopSnapshot? ParseSnapshot(
        string? json, DesktopPageTarget target, DesktopPageVersion pageVersion, DesktopSnapshotOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(json) || pageVersion.Value <= 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var nodeCount = root.TryGetProperty("nodeCount", out var nodes) && nodes.TryGetInt32(out var parsedNodes)
                ? parsedNodes
                : -1;
            if (nodeCount < 0)
            {
                return null;
            }

            var truncated = root.TryGetProperty("truncated", out var flag)
                && flag.ValueKind == JsonValueKind.True;

            var snapshot = new DesktopSnapshot(
                target,
                ReadOptionalString(root, "domText"),
                ReadOptionalString(root, "accessibilityTree"),
                null,
                truncated,
                nodeCount,
                pageVersion);

            // 再按请求预算收敛一次：预算由服务侧兜底，但实现方也应尽早收敛，避免把超大字符串搬过线程。
            return DesktopCapabilityBudgets.Apply(snapshot, options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析定位结果；结构不符返回 <c>null</c>（fail closed）。</summary>
    public static DesktopLocateResult? ParseLocate(
        string? json, BrowserLocateRequest request, DesktopPageVersion pageVersion)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(json) || pageVersion.Value <= 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("elements", out var elements)
                || elements.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var parsed = new List<DesktopElementRef>(elements.GetArrayLength());
            foreach (var element in elements.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                var reference = ReadRequiredString(element, "ref");
                var tag = ReadRequiredString(element, "tag");
                if (reference is null || tag is null)
                {
                    return null;
                }

                parsed.Add(new DesktopElementRef(
                    reference,
                    tag,
                    pageVersion,
                    ReadOptionalString(element, "role"),
                    ReadOptionalString(element, "name"),
                    ReadOptionalString(element, "text"),
                    visible: !element.TryGetProperty("visible", out var visible) || visible.ValueKind == JsonValueKind.True,
                    enabled: !element.TryGetProperty("enabled", out var enabled) || enabled.ValueKind == JsonValueKind.True,
                    isChecked: element.TryGetProperty("checked", out var isChecked) && isChecked.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? isChecked.GetBoolean()
                        : null));
            }

            var truncated = root.TryGetProperty("truncated", out var flag) && flag.ValueKind == JsonValueKind.True;
            var result = new DesktopLocateResult(request.Target, request.Locator, parsed, truncated, pageVersion);

            return DesktopCapabilityBudgets.Apply(result, request.MaxResults);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // 元素引用不合法（空 ref/tag）⇒ 整体作废，不把坏引用交给上层。
            return null;
        }
    }

    private static string JsonString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string? ReadRequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
