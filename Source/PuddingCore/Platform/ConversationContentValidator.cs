using System.Text.RegularExpressions;
using PuddingCode.Models;

namespace PuddingCode.Platform;

/// <summary>
/// SubmitTurn 内容块合同校验（ADR-077 §4.1/§5.1）。Controller 与 Handler 共用，
/// Web、Camera Snapshot 和 Connector 在 admission 前归一为同一规则。
/// </summary>
public static partial class ConversationContentValidator
{
    /// <summary>产品上限：每轮最多图片数。</summary>
    public const int MaxImagesPerTurn = 8;

    /// <summary>每条 text part 的最大字符数（与旧 MessageText 投影一致量级）。</summary>
    public const int MaxTextPartLength = 100_000;

    [GeneratedRegex("^vision-[a-f0-9]{32}$", RegexOptions.Compiled)]
    private static partial Regex VisionArtifactIdRegex();

    /// <summary>返回首个违反合同的错误消息；合法返回 null。允许纯图片消息（服务端不伪造用户正文）。</summary>
    public static string? Validate(IReadOnlyList<ContentPart>? content)
    {
        if (content is null || content.Count == 0)
            return "At least one content part is required.";

        var imageCount = 0;
        var hasNonEmptyText = false;
        foreach (var part in content)
        {
            if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(part.Text))
                    return "Text content parts cannot be empty.";
                if (part.Text.Length > MaxTextPartLength)
                    return $"Text content parts cannot exceed {MaxTextPartLength} characters.";
                hasNonEmptyText = true;
                continue;
            }

            if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(part.ArtifactId)
                    || !VisionArtifactIdRegex().IsMatch(part.ArtifactId))
                    return "Image content parts require a workspace vision artifactId (vision-<32hex>).";
                if (!VisionContentPartDetails.IsValid(part.Detail))
                    return "Image detail must be 'original' (default) or 'low'.";
                imageCount++;
                continue;
            }

            return $"Content part type '{part.Type}' is not supported; use 'text' or 'image'.";
        }

        if (!hasNonEmptyText && imageCount == 0)
            return "At least one non-empty text or image content part is required.";

        if (imageCount > MaxImagesPerTurn)
            return $"A turn accepts at most {MaxImagesPerTurn} image content parts.";

        return null;
    }

    /// <summary>把 HTTP 合同部件转换为 canonical LLM 部件（text 顺序保持）。</summary>
    public static IReadOnlyList<LlmContentPart>? ToLlmContentParts(
        IReadOnlyList<ContentPart>? content,
        out string flattenedText)
    {
        if (content is null || content.Count == 0)
        {
            flattenedText = string.Empty;
            return null;
        }

        var parts = new List<LlmContentPart>(content.Count);
        var hasMedia = false;
        foreach (var part in content)
        {
            if (string.Equals(part.Type, "text", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(new LlmTextPart(part.Text!));
                continue;
            }

            if (string.Equals(part.Type, "image", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(new LlmImagePart(
                    part.ArtifactId!,
                    string.IsNullOrWhiteSpace(part.Detail)
                        ? VisionContentPartDetails.Original
                        : part.Detail!));
                hasMedia = true;
            }
        }

        flattenedText = ContentPartsEnvelope.FlattenText(parts);
        return hasMedia ? parts : null;
    }
}
