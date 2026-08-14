using System.Text.Json;

namespace PuddingCode.Tools.Definitions;

/// <summary>
/// 工具结果的无损 JSON 物化器（lossless JSON materializer）。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 约束 3 ——
/// Registry 对工具主体返回值做 lossless JSON materialize、output schema 校验和冻结，再执行 renderer。
/// 本类型只负责其中第一步（materialize），不涉及 render/finalize/pipeline。
/// </para>
/// <para>
/// lossless 语义：物化产出的 canonical <see cref="JsonElement"/> 是「事实」，必须原样保留输入值：
/// 不截断文本、不丢失数字精度、不做任何展示层转换（不转 Markdown、不转 UI 卡片、不裁剪、不四舍五入）。
/// 后续的 output schema 校验与 renderer 都建立在该 canonical 值之上。
/// </para>
/// </summary>
public static class ToolResultMaterializer
{
    private static readonly JsonElement NullElement = CreateNullElement();

    /// <summary>
    /// 把工具主体返回的任意值无损物化为 canonical <see cref="JsonElement"/>。
    /// <para>
    /// 转换规则：
    /// <list type="bullet">
    /// <item>输入已是 <see cref="JsonElement"/> → 直接透传（已是 canonical，不做二次转换）；</item>
    /// <item>输入 <c>null</c> → 返回 <see cref="JsonValueKind.Null"/> 的 JsonElement；</item>
    /// <item>
    /// 输入 <see cref="string"/> → 先 <c>Trim()</c> 后尝试 <see cref="JsonDocument.Parse(string)"/>；
    /// 解析成功则 <c>Clone()</c> 根元素返回（保留结构化 JSON）；解析失败（非法 JSON 或空白串）
    /// 则将其作为 JSON string 值包装返回（保留原文本，不丢失前导/尾随空白与原始内容）；
    /// </item>
    /// <item>其他 CLR object（record/class/<see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>/
    /// <see cref="System.Collections.Generic.List{T}"/> / 数组 / 原始类型如 int/bool 等）
    /// → <see cref="JsonSerializer.SerializeToElement(object, Type)"/> 按运行时类型序列化，不丢精度。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="value">工具主体返回的任意值，可为 null。</param>
    /// <returns>canonical <see cref="JsonElement"/>，是工具结果的唯一事实源。</returns>
    public static JsonElement Materialize(object? value)
    {
        if (value is JsonElement element)
        {
            return element;
        }

        if (value is null)
        {
            return NullElement;
        }

        if (value is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0 && TryParseStructured(trimmed, out var parsed))
            {
                // 合法 JSON：保留结构化形式（对象/数组/标量）。
                return parsed;
            }

            // 非法 JSON（或纯空白）：作为 JSON string 值包装原文本，不丢失原文本。
            return JsonSerializer.SerializeToElement(text);
        }

        // 其他 CLR 值：按运行时类型无损序列化（数字精度不丢失，文本不截断）。
        return JsonSerializer.SerializeToElement(value, value.GetType());
    }

    private static bool TryParseStructured(string json, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }

    private static JsonElement CreateNullElement()
    {
        using var doc = JsonDocument.Parse("null");
        return doc.RootElement.Clone();
    }
}
