using System.Text.Json;

namespace PuddingBrowser.Automation;

/// <summary>
/// **页内 DOM 变更计数器**（设计 §8.2 的失效键之一：页内变更后旧截图坐标不再可信）。
///
/// 机制：截图（观察）时在页面内**幂等**安装一个 `MutationObserver`，把变更次数累加到
/// `window.__puddingDomRevision`；之后带 observationId 的坐标请求读回该计数比对。
/// 缺计数或计数为负 ⇒ **拒绝**（不猜"没变过"）。
/// </summary>
public static class ScreenshotDomRevision
{
    /// <summary>计数所在的全局属性名（读取与安装必须一致）。</summary>
    public const string CounterProperty = "__puddingDomRevision";

    /// <summary>
    /// 幂等安装脚本：已安装则**不重复安装**（重复会漏计/重计）；返回当前计数。
    /// </summary>
    public const string InstallScript = """
        (() => {
          if (window.__puddingDomRevisionInstalled) { return window.__puddingDomRevision; }
          window.__puddingDomRevision = 1;
          window.__puddingDomRevisionInstalled = true;
          const observer = new MutationObserver(() => { window.__puddingDomRevision += 1; });
          observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true, characterData: true });
          return window.__puddingDomRevision;
        })()
        """;

    /// <summary>读取当前计数（供带 observationId 的坐标请求比对）。</summary>
    public static bool TryRead(JsonElement evaluationResult, out long revision, out string reason)
    {
        revision = 0;
        reason = string.Empty;

        if (evaluationResult.ValueKind != JsonValueKind.Number)
        {
            reason = $"the in-page DOM revision counter ({CounterProperty}) is missing or not a number";
            return false;
        }

        if (!evaluationResult.TryGetInt64(out revision) || revision < 0)
        {
            reason = $"the in-page DOM revision counter ({CounterProperty}) must be a non-negative integer";
            return false;
        }

        return true;
    }

    /// <summary>读取计数的 CDP 表达式（配合 <c>Runtime.evaluate</c> + <c>returnByValue</c>）。</summary>
    public const string CounterExpression = "window.__puddingDomRevision";

    /// <summary>
    /// 从 <c>Runtime.evaluate</c> 的返回里取计数：形如 <c>{ "result": { "type": "number", "value": 12 } }</c>。
    /// **缺 result/value、类型不是 number、负数/非整数一律拒绝**（绝不把"读不到"当成"没变过"）。
    /// </summary>
    public static bool TryReadEvaluationResult(JsonElement evaluationResult, out long revision, out string reason)
    {
        revision = 0;
        reason = string.Empty;

        if (evaluationResult.ValueKind != JsonValueKind.Object
            || !evaluationResult.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("value", out var value))
        {
            reason = $"the in-page DOM revision counter ({CounterProperty}) could not be read; " +
                "an observation must be established by a screenshot before its coordinates can be used";
            return false;
        }

        return TryRead(value, out revision, out reason);
    }
}