namespace PuddingCode.Models;

/// <summary>
/// 统一的多模态渲染入口（ADR-077）：Gateway 只消费这里产出的有序部件。
/// canonical 来源是 <see cref="ChatMessage.ContentParts"/>；仅当部件缺失而旧
/// <see cref="ChatMessage.VisualArtifactIds"/> 列表存在时（未迁移的构造方/历史测试），
/// 按原顺序派生 original detail 的图片部件，保证旧路径不静默丢图。
/// </summary>
public static class ChatMessageMultimodalNormalizer
{
    /// <summary>消息内有序文本/图片部件；纯文本消息返回 null。</summary>
    public static IReadOnlyList<LlmContentPart>? GetEffectiveContentParts(ChatMessage message)
    {
        if (message.ContentParts is { Count: > 0 } parts)
            return parts;

        if (message.VisualArtifactIds is not { Count: > 0 } artifactIds)
            return null;

        var derived = new List<LlmContentPart>(artifactIds.Count + 1);
        if (!string.IsNullOrEmpty(message.Content))
            derived.Add(new LlmTextPart(message.Content!));
        foreach (var artifactId in artifactIds)
            derived.Add(new LlmImagePart(artifactId));
        return derived;
    }

    /// <summary>消息引用的全部图片部件（按 canonical 顺序）。</summary>
    public static IReadOnlyList<LlmImagePart> GetImageParts(ChatMessage message)
        => GetEffectiveContentParts(message)?.OfType<LlmImagePart>().ToList() ?? [];
}
