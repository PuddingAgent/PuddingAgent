using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingCode.Runtime;

/// <summary>
/// 工具调用身份值对象（T00 最小子集）。
/// <para>
/// 契约来源：Docs/deepseek-harness-tool-system-alignment-2026-08-14.md B:227-254。
/// 一次调用只有一个外部 callId，跨层不得重新生成；一旦进入 Agent Loop 即不可更换。
/// Provider 缺失时由协议适配器稳定合成（见 <see cref="NewToolCallId"/>）。
/// </para>
/// <para>
/// 该类型序列化为裸 JSON 字符串（如 "toolCallId":"a1b2..."），因此可直接作为事件
/// Payload 字段或信封字段使用，无需客户端了解包装结构。
/// </para>
/// </summary>
[JsonConverter(typeof(ToolCallIdJsonConverter))]
public readonly record struct ToolCallId(string Value)
{
    /// <summary>生成一个新的调用 id（GUID 小写、无连字符）。</summary>
    public static ToolCallId NewToolCallId() => new(Guid.NewGuid().ToString("N"));

    /// <summary>解析调用 id；空/纯空白值抛 <see cref="ArgumentException"/>。</summary>
    public static ToolCallId Parse(string value)
    {
        if (!TryParse(value, out var result))
        {
            throw new ArgumentException(
                "ToolCallId 不能为 null、空或纯空白字符串。",
                nameof(value));
        }

        return result;
    }

    /// <summary>尝试解析调用 id；空/纯空白值返回 false。</summary>
    public static bool TryParse(string? value, out ToolCallId result)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            result = default;
            return false;
        }

        result = new ToolCallId(value);
        return true;
    }

    /// <summary>是否为未设置/空的调用 id。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    /// <summary>返回裸 id 字符串。</summary>
    public override string ToString() => Value;

    /// <summary>序列化为裸字符串 / 从裸字符串反序列化。</summary>
    private sealed class ToolCallIdJsonConverter : JsonConverter<ToolCallId>
    {
        public override ToolCallId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetString() ?? string.Empty);

        public override void Write(Utf8JsonWriter writer, ToolCallId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);
    }
}
