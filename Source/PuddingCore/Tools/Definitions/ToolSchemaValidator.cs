using System.Text.Json;

namespace PuddingCode.Tools.Definitions;

/// <summary>
/// 工具输入/输出 schema 校验器。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6 约束 3 ——
/// Registry 对返回值做 lossless JSON materialize、output schema 校验和冻结，再执行 renderer。
/// 本类型只负责其中的 schema 校验（input/output），不涉及 render/finalize/pipeline。
/// </para>
/// <para>
/// 校验逻辑完全复用 <see cref="JsonSchema.Validate(JsonElement)"/>（从根路径 <c>$</c> 开始），
/// 返回 <c>$.xxx: message</c> 格式的路径错误列表；空列表表示通过。
/// 不复制、不重写任何 schema 校验逻辑。
/// </para>
/// </summary>
public static class ToolSchemaValidator
{
    /// <summary>
    /// 校验 <paramref name="arguments"/> 是否符合 <paramref name="definition"/> 的 <see cref="ToolDefinition.InputSchema"/>。
    /// </summary>
    /// <param name="definition">工具定义，携带 input schema。不能为 null。</param>
    /// <param name="arguments">待校验的 canonical 输入 JSON 值。</param>
    /// <returns>路径错误列表（<c>$.xxx: message</c>），空列表表示通过。</returns>
    public static IReadOnlyList<string> ValidateInput(ToolDefinition definition, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.InputSchema is null)
        {
            return new[] { "$.input_schema: required property is missing" };
        }

        return definition.InputSchema.Validate(arguments);
    }

    /// <summary>
    /// 校验 <paramref name="result"/> 是否符合 <paramref name="definition"/> 的 <see cref="ToolOutputDefinition.Schema"/>。
    /// </summary>
    /// <param name="definition">工具定义，携带 output 合同。不能为 null。</param>
    /// <param name="result">待校验的 canonical 输出 JSON 值。</param>
    /// <returns>路径错误列表（<c>$.xxx: message</c>），空列表表示通过。</returns>
    public static IReadOnlyList<string> ValidateOutput(ToolDefinition definition, JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.Output is null)
        {
            return new[] { "$.output: required property is missing" };
        }

        if (definition.Output.Schema is null)
        {
            return new[] { "$.output.schema: required property is missing" };
        }

        return definition.Output.Schema.Validate(result);
    }

    /// <summary>便捷方法：输入是否通过校验（等价于 <see cref="ValidateInput"/> 返回空列表）。</summary>
    public static bool IsValidInput(ToolDefinition definition, JsonElement arguments)
        => ValidateInput(definition, arguments).Count == 0;

    /// <summary>便捷方法：输出是否通过校验（等价于 <see cref="ValidateOutput"/> 返回空列表）。</summary>
    public static bool IsValidOutput(ToolDefinition definition, JsonElement result)
        => ValidateOutput(definition, result).Count == 0;
}
