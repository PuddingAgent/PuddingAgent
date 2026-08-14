using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingCode.Models;

namespace PuddingCode.Tools.Definitions;

/// <summary>
/// JSON Schema 标量类型。覆盖工具输入/输出 Schema 的核心类型系统。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6.1（Schema 能力清单）：
/// <c>string/number/integer/boolean/null/array/object</c>。
/// </para>
/// </summary>
public enum JsonSchemaType
{
    String,
    Number,
    Integer,
    Boolean,
    Null,
    Array,
    Object,
}

/// <summary>
/// Core 不可变 Schema AST 节点。作为工具输入/输出 Schema 的统一核心类型。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md §6.1（Schema 能力清单）。
/// 需要支持：nested properties/items、required、enum、const、oneOf、
/// minimum/maximum、minLength/maxLength、pattern、显式 additionalProperties，
/// 以及属性路径明确的验证错误（如 <c>$.timeout_seconds: must be &lt;= 600</c>）。
/// </para>
/// <para>
/// 该类型是 <see cref="ToolParameterSchema"/> 的超集：现有极简 schema（name/type/description/required）
/// 可通过 <see cref="FromToolParameterSchema"/> 无损转换为 object 节点，作为 P0-B 迁移源保留。
/// 集合属性一律暴露为 IReadOnlyList/IReadOnlyDictionary，不暴露可变集合。
/// </para>
/// </summary>
public sealed record JsonSchema
{
    /// <summary>类型约束；null 表示未限定（任意类型）。</summary>
    public JsonSchemaType? Type { get; init; }

    /// <summary>人类可读描述，不参与验证，仅用于模型/UI 展示。</summary>
    public string? Description { get; init; }

    /// <summary>枚举约束：值必须等于其中之一。仅对值本身，不等同于 oneOf。</summary>
    public IReadOnlyList<JsonElement>? Enum { get; init; }

    /// <summary>常量约束：值必须与该 JSON 值深相等。</summary>
    public JsonElement? Const { get; init; }

    /// <summary>string 专属：最小长度（包含）。</summary>
    public int? MinLength { get; init; }

    /// <summary>string 专属：最大长度（包含）。</summary>
    public int? MaxLength { get; init; }

    /// <summary>string 专属：正则约束（.NET 正则语法）。</summary>
    public string? Pattern { get; init; }

    /// <summary>number/integer 专属：最小值（包含）。</summary>
    public double? Minimum { get; init; }

    /// <summary>number/integer 专属：最大值（包含）。</summary>
    public double? Maximum { get; init; }

    /// <summary>array 专属：数组元素 schema，嵌套。</summary>
    public JsonSchema? Items { get; init; }

    /// <summary>object 专属：属性名 → 属性 schema。</summary>
    public IReadOnlyDictionary<string, JsonSchema>? Properties { get; init; }

    /// <summary>object 专属：必须出现的属性名列表。</summary>
    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>
    /// object 专属：额外属性策略（三态，与 <see cref="AdditionalPropertiesSchema"/> 配合）。
    /// <list type="bullet">
    /// <item>null = 未指定，额外属性默认允许（不校验）；</item>
    /// <item>true = 允许任意额外属性；</item>
    /// <item>false = 禁止额外属性（出现即报错）。</item>
    /// </list>
    /// </summary>
    public bool? AdditionalProperties { get; init; }

    /// <summary>
    /// object 专属：额外属性必须匹配的 schema。非 null 表示“指定 schema”的第三种表达方式，
    /// 优先级高于 <see cref="AdditionalProperties"/> 的布尔值。
    /// </summary>
    public JsonSchema? AdditionalPropertiesSchema { get; init; }

    /// <summary>组合约束：值必须恰好匹配其中一个分支。</summary>
    public IReadOnlyList<JsonSchema>? OneOf { get; init; }

    /// <summary>构造一个仅带类型约束的节点。</summary>
    public static JsonSchema OfType(JsonSchemaType type) => new() { Type = type };

    /// <summary>构造 string 节点。</summary>
    public static JsonSchema String(
        string? description = null,
        int? minLength = null,
        int? maxLength = null,
        string? pattern = null,
        IReadOnlyList<JsonElement>? @enum = null,
        JsonElement? @const = null) => new()
    {
        Type = JsonSchemaType.String,
        Description = description,
        MinLength = minLength,
        MaxLength = maxLength,
        Pattern = pattern,
        Enum = @enum,
        Const = @const,
    };

    /// <summary>构造 integer 节点。</summary>
    public static JsonSchema Integer(
        string? description = null,
        double? minimum = null,
        double? maximum = null,
        IReadOnlyList<JsonElement>? @enum = null,
        JsonElement? @const = null) => new()
    {
        Type = JsonSchemaType.Integer,
        Description = description,
        Minimum = minimum,
        Maximum = maximum,
        Enum = @enum,
        Const = @const,
    };

    /// <summary>构造 number 节点。</summary>
    public static JsonSchema Number(
        string? description = null,
        double? minimum = null,
        double? maximum = null,
        IReadOnlyList<JsonElement>? @enum = null,
        JsonElement? @const = null) => new()
    {
        Type = JsonSchemaType.Number,
        Description = description,
        Minimum = minimum,
        Maximum = maximum,
        Enum = @enum,
        Const = @const,
    };

    /// <summary>构造 boolean 节点。</summary>
    public static JsonSchema Boolean(
        string? description = null,
        IReadOnlyList<JsonElement>? @enum = null,
        JsonElement? @const = null) => new()
    {
        Type = JsonSchemaType.Boolean,
        Description = description,
        Enum = @enum,
        Const = @const,
    };

    /// <summary>构造 array 节点。</summary>
    public static JsonSchema Array(JsonSchema items, string? description = null) => new()
    {
        Type = JsonSchemaType.Array,
        Items = items,
        Description = description,
    };

    /// <summary>构造 object 节点。</summary>
    public static JsonSchema Object(
        IReadOnlyDictionary<string, JsonSchema>? properties = null,
        IReadOnlyList<string>? required = null,
        string? description = null,
        bool? additionalProperties = null,
        JsonSchema? additionalPropertiesSchema = null) => new()
    {
        Type = JsonSchemaType.Object,
        Properties = properties,
        Required = required,
        Description = description,
        AdditionalProperties = additionalProperties,
        AdditionalPropertiesSchema = additionalPropertiesSchema,
    };

    /// <summary>
    /// 将现有极简 <see cref="ToolParameterSchema"/> 无损转换为 object 节点。
    /// <para>
    /// 映射：<see cref="ToolParameter.Name"/> → Properties key；
    /// <see cref="ToolParameter.Type"/> 字符串 → <see cref="JsonSchemaType"/>；
    /// <see cref="ToolParameter.Description"/> → Description；
    /// <see cref="ToolParameterSchema.Required"/> → Required。
    /// </para>
    /// <para>
    /// 类型字符串映射（大小写不敏感）：<c>string</c>→String、<c>integer</c>→Integer、
    /// <c>number</c>→Number、<c>boolean</c>→Boolean、<c>array</c>→Array、<c>object</c>→Object。
    /// 未知类型按 Object 处理（与 <c>ToolDescriptorFactory.MapClrTypeToJsonType</c> 的默认兜底一致），
    /// 以保持迁移路径对历史数据的宽容，不抛异常。
    /// </para>
    /// </summary>
    public static JsonSchema FromToolParameterSchema(ToolParameterSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var properties = new Dictionary<string, JsonSchema>(StringComparer.Ordinal);
        foreach (var parameter in schema.Properties)
        {
            properties[parameter.Name] = new JsonSchema
            {
                Type = MapTypeString(parameter.Type),
                Description = parameter.Description,
            };
        }

        return new JsonSchema
        {
            Type = JsonSchemaType.Object,
            Properties = properties,
            Required = schema.Required is { Count: > 0 } ? schema.Required : null,
        };
    }

    /// <summary>将 ToolParameterSchema 的类型字符串映射为 <see cref="JsonSchemaType"/>。未知类型按 Object 处理。</summary>
    private static JsonSchemaType MapTypeString(string type)
        => (type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "string" => JsonSchemaType.String,
            "integer" => JsonSchemaType.Integer,
            "number" => JsonSchemaType.Number,
            "boolean" => JsonSchemaType.Boolean,
            "array" => JsonSchemaType.Array,
            "object" => JsonSchemaType.Object,
            _ => JsonSchemaType.Object,
        };

    /// <summary>从根路径 <c>$</c> 开始验证。</summary>
    public IReadOnlyList<string> Validate(JsonElement value) => Validate(value, "$");

    /// <summary>
    /// 验证 <paramref name="value"/> 是否满足本节点约束，返回 <c>$.xxx: message</c> 格式的路径错误列表。
    /// 空列表表示通过。错误会携带具体属性路径（如 <c>$.timeout_seconds: value must be &lt;= 600</c>）。
    /// </summary>
    /// <param name="value">待验证的 JSON 值。</param>
    /// <param name="jsonPointer">当前值的 JSON 路径前缀，如 <c>$</c>、<c>$.field</c>、<c>$.items[0]</c>。</param>
    public IReadOnlyList<string> Validate(JsonElement value, string jsonPointer)
    {
        var errors = new List<string>();
        ValidateCore(value, jsonPointer, errors);
        return errors;
    }

    private void ValidateCore(JsonElement value, string jsonPointer, List<string> errors)
    {
        if (OneOf is { Count: > 0 })
        {
            var matched = 0;
            foreach (var branch in OneOf)
            {
                var branchErrors = new List<string>();
                branch.ValidateCore(value, jsonPointer, branchErrors);
                if (branchErrors.Count == 0)
                {
                    matched++;
                }
            }

            if (matched != 1)
            {
                errors.Add(
                    $"{jsonPointer}: value must match exactly one of {OneOf.Count} oneOf branches (matched {matched})");
            }

            // oneOf 命中即视为本节点通过，不再叠加标量/结构约束。
            return;
        }

        if (Type is { } expectedType && !MatchesType(value, expectedType))
        {
            errors.Add($"{jsonPointer}: expected {TypeName(expectedType)}, got {KindName(value.ValueKind)}");
            return;
        }

        if (Const is { } constValue && !JsonElement.DeepEquals(value, constValue))
        {
            errors.Add($"{jsonPointer}: value must equal the const value");
        }

        if (Enum is { Count: > 0 } enumValues && !enumValues.Any(e => JsonElement.DeepEquals(value, e)))
        {
            errors.Add($"{jsonPointer}: value is not one of the allowed enum values");
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? string.Empty;

            if (MinLength is { } minLength && text.Length < minLength)
            {
                errors.Add($"{jsonPointer}: string length must be >= {minLength}");
            }

            if (MaxLength is { } maxLength && text.Length > maxLength)
            {
                errors.Add($"{jsonPointer}: string length must be <= {maxLength}");
            }

            if (Pattern is { } pattern && !Regex.IsMatch(text, pattern))
            {
                errors.Add($"{jsonPointer}: value does not match pattern '{pattern}'");
            }
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            if (Minimum is { } minimum && number < minimum)
            {
                errors.Add($"{jsonPointer}: value must be >= {minimum}");
            }

            if (Maximum is { } maximum && number > maximum)
            {
                errors.Add($"{jsonPointer}: value must be <= {maximum}");
            }
        }

        if (value.ValueKind == JsonValueKind.Array && Items is not null)
        {
            var index = 0;
            foreach (var element in value.EnumerateArray())
            {
                Items.ValidateCore(element, $"{jsonPointer}[{index}]", errors);
                index++;
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (Required is { Count: > 0 })
            {
                foreach (var name in Required)
                {
                    if (!value.TryGetProperty(name, out _))
                    {
                        errors.Add($"{jsonPointer}.{name}: required property is missing");
                    }
                }
            }

            foreach (var property in value.EnumerateObject())
            {
                if (Properties is not null && Properties.TryGetValue(property.Name, out var propertySchema))
                {
                    propertySchema.ValidateCore(property.Value, $"{jsonPointer}.{property.Name}", errors);
                }
                else if (AdditionalProperties == false)
                {
                    errors.Add($"{jsonPointer}.{property.Name}: unexpected property (additionalProperties is false)");
                }
                else if (AdditionalPropertiesSchema is not null)
                {
                    AdditionalPropertiesSchema.ValidateCore(property.Value, $"{jsonPointer}.{property.Name}", errors);
                }
            }
        }
    }

    private static bool MatchesType(JsonElement value, JsonSchemaType type) => type switch
    {
        JsonSchemaType.String => value.ValueKind == JsonValueKind.String,
        JsonSchemaType.Number => value.ValueKind == JsonValueKind.Number,
        JsonSchemaType.Integer => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        JsonSchemaType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        JsonSchemaType.Null => value.ValueKind == JsonValueKind.Null,
        JsonSchemaType.Array => value.ValueKind == JsonValueKind.Array,
        JsonSchemaType.Object => value.ValueKind == JsonValueKind.Object,
        _ => true,
    };

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _))
        {
            return true;
        }

        if (value.TryGetDecimal(out var number))
        {
            return number == decimal.Truncate(number);
        }

        if (value.TryGetDouble(out var floating))
        {
            return floating == Math.Truncate(floating);
        }

        return false;
    }

    private static string TypeName(JsonSchemaType type) => type switch
    {
        JsonSchemaType.String => "string",
        JsonSchemaType.Number => "number",
        JsonSchemaType.Integer => "integer",
        JsonSchemaType.Boolean => "boolean",
        JsonSchemaType.Null => "null",
        JsonSchemaType.Array => "array",
        JsonSchemaType.Object => "object",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True => "boolean",
        JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.Undefined => "undefined",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
