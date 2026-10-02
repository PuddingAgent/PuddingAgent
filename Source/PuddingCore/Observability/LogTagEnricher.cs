using Serilog.Core;
using Serilog.Events;

namespace PuddingCode.Observability;

/// <summary>
/// 给每条日志补一个**显式 tag**，让"打印之后能追踪"不依赖人肉看时间戳。
///
/// <para>
/// 背景：日志此前按组件分文件（<c>logs/components/&lt;component&gt;</c>），**行内没有 tag** ——
/// 一旦把多个文件拼起来看（或看 <c>logs/system</c> 的汇总文件），就分不清哪一行来自哪个组件。
/// 本 enricher 保证每行都有 <c>[tag:...]</c>。
/// </para>
/// <para>
/// tag 取值优先级（<see cref="DeriveTag"/>，纯函数便于测试）：
/// ① <c>Component</c> 属性（组件专用 sink 的过滤键，也是最有追踪价值的粒度）；
/// ② <c>SourceContext</c> 的**短名**（全限定类名的最后一段，避免每行几十字符的噪声）；
/// ③ 兜底 <c>"core"</c>（例如宿主自身的启动日志）。
/// </para>
/// </summary>
public sealed class LogTagEnricher : ILogEventEnricher
{
    /// <summary>属性名：日志模板用 <c>{Tag}</c> 引用。</summary>
    public const string PropertyName = "Tag";

    /// <summary>组件属性名（与 <see cref="SerilogComponentFilterExtensions.ByIncludingComponent"/> 的过滤键一致）。</summary>
    public const string ComponentPropertyName = "Component";

    /// <summary>兜底 tag：既没有组件也没有 SourceContext 时的宿主级日志。</summary>
    public const string FallbackTag = "core";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(propertyFactory);

        var component = ReadScalar(logEvent, ComponentPropertyName);
        var sourceContext = ReadScalar(logEvent, "SourceContext");
        var tag = DeriveTag(component, sourceContext);

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(PropertyName, tag));
    }

    /// <summary>
    /// tag 推导（纯函数）：
    /// 组件优先；其次 SourceContext 的短名（<c>A.B.C</c> ⇒ <c>C</c>）；都没有 ⇒ <see cref="FallbackTag"/>。
    /// 空白值按"没有"处理，避免出现 <c>[tag:   ]</c>。
    /// </summary>
    /// <param name="component">组件名（可空）。</param>
    /// <param name="sourceContext">SourceContext 全名（可空）。</param>
    public static string DeriveTag(string? component, string? sourceContext)
    {
        if (!string.IsNullOrWhiteSpace(component))
        {
            return component.Trim();
        }

        if (!string.IsNullOrWhiteSpace(sourceContext))
        {
            var trimmed = sourceContext.Trim();
            var lastDot = trimmed.LastIndexOf('.');
            var shortName = lastDot >= 0 && lastDot < trimmed.Length - 1 ? trimmed[(lastDot + 1)..] : trimmed;
            return string.IsNullOrWhiteSpace(shortName) ? FallbackTag : shortName;
        }

        return FallbackTag;
    }

    private static string? ReadScalar(LogEvent logEvent, string propertyName)
        => logEvent.Properties.TryGetValue(propertyName, out var value)
            && value is ScalarValue scalar
            && scalar.Value is string text
                ? text
                : null;
}
