using System.Text.Json;
using System.Text.Json.Serialization;

namespace PuddingCode.Models;

/// <summary>
/// LLM 消息的有序内容部件（ADR-077）。图片只携带 Workspace Artifact 引用，
/// 字节在 Provider invocation boundary 才解析；客户端 Data URL、外部 URL、
/// 本地绝对路径都不是合法的部件字段。
/// </summary>
public abstract record LlmContentPart
{
    /// <summary>部件类型判别值：text | image。</summary>
    public abstract string Type { get; }
}

/// <summary>文本部件。</summary>
public sealed record LlmTextPart(string Text) : LlmContentPart
{
    public override string Type => "text";
}

/// <summary>
/// 图片部件。detail 缺省 original；low 只在用户或明确策略选择时出现，
/// 且永远生成受控副本，不覆盖 canonical Artifact。
/// </summary>
public sealed record LlmImagePart(string ArtifactId, string Detail = VisionContentPartDetails.Original)
    : LlmContentPart
{
    public override string Type => "image";
}

/// <summary>图片 detail 的 canonical 取值。Provider 序列化时由 Gateway 映射（如 original→high）。</summary>
public static class VisionContentPartDetails
{
    public const string Original = "original";
    public const string Low = "low";

    public static bool IsValid(string? detail)
        => detail is null
           || string.Equals(detail, Original, StringComparison.Ordinal)
           || string.Equals(detail, Low, StringComparison.Ordinal);
}

/// <summary>
/// 多模态内容部件的版本化信封（v:1）。ConversationAcceptanceStore、DB/JSONL 水合
/// 和事件 payload 共用同一编解码，禁止各自发明序列化格式。
/// </summary>
public static class ContentPartsEnvelope
{
    public const int Version = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Encode(IReadOnlyList<LlmContentPart> parts)
    {
        var envelope = new ContentPartsEnvelopeDto
        {
            V = Version,
            Parts = parts.Select(ToDto).ToList(),
        };
        return JsonSerializer.Serialize(envelope, SerializerOptions);
    }

    public static IReadOnlyList<LlmContentPart>? Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        ContentPartsEnvelopeDto? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ContentPartsEnvelopeDto>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (envelope?.Parts is not { Count: > 0 } dtos || envelope.V != Version)
            return null;

        var parts = new List<LlmContentPart>(dtos.Count);
        foreach (var dto in dtos)
        {
            switch (dto.Type)
            {
                case "text" when !string.IsNullOrEmpty(dto.Text):
                    parts.Add(new LlmTextPart(dto.Text!));
                    break;
                case "image" when !string.IsNullOrWhiteSpace(dto.ArtifactId):
                    parts.Add(new LlmImagePart(
                        dto.ArtifactId!,
                        string.IsNullOrWhiteSpace(dto.Detail)
                            ? VisionContentPartDetails.Original
                            : dto.Detail!));
                    break;
                default:
                    return null;
            }
        }

        return parts.Count > 0 ? parts : null;
    }

    /// <summary>text part 的稳定拼接 — ChatMessages.Content 文本投影与 UI 摘要使用。</summary>
    public static string FlattenText(IReadOnlyList<LlmContentPart> parts)
        => string.Concat(parts.OfType<LlmTextPart>().Select(p => p.Text));

    private static ContentPartDto ToDto(LlmContentPart part) => part switch
    {
        LlmTextPart text => new ContentPartDto { Type = "text", Text = text.Text },
        LlmImagePart image => new ContentPartDto
        {
            Type = "image",
            ArtifactId = image.ArtifactId,
            Detail = image.Detail,
        },
        _ => throw new NotSupportedException($"Unknown LlmContentPart type {part.GetType().Name}."),
    };

    private sealed class ContentPartsEnvelopeDto
    {
        public int V { get; init; }
        public List<ContentPartDto> Parts { get; init; } = [];
    }

    private sealed class ContentPartDto
    {
        public string Type { get; init; } = "";
        public string? Text { get; init; }
        public string? ArtifactId { get; init; }
        public string? Detail { get; init; }
    }
}
