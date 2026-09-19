using PuddingCode.Models;

namespace PuddingCode.Core;

/// <summary>Request-local projection only; canonical history and source artifacts are never changed.</summary>
public sealed class VisionTextContinuation(string? currentInputHash, int maxImages)
{
    public bool Active { get; private set; }

    public bool TryActivate(IReadOnlyList<ChatMessage> messages)
    {
        var boundary = FindBoundary(messages);
        if (Active || boundary < 0 || !messages.Take(boundary).Any(HasImages)
            || messages.Skip(boundary).Any(HasImages))
            return false;
        Active = true;
        return true;
    }

    public List<ChatMessage> Prepare(IReadOnlyList<ChatMessage> messages)
    {
        if (!Active && messages.Sum(m => ChatMessageMultimodalNormalizer.GetImageParts(m).Count) > maxImages)
            TryActivate(messages);
        var boundary = FindBoundary(messages);
        if (!Active || boundary < 0)
            return messages.ToList();
        return messages.Select((message, index) =>
        {
            if (index >= boundary || !HasImages(message)) return message;
            var parts = ChatMessageMultimodalNormalizer.GetEffectiveContentParts(message)!
                .Select(part => part is LlmImagePart image
                    ? (LlmContentPart)new LlmTextPart($"\n[历史图片 {image.ArtifactId}：因图片请求限制，本次仅保留引用，未发送像素。原图仍保留；需要查看时请分批使用 image_reader 读取，不要假定已看到图片。]\n")
                    : part).ToList();
            return message with { ContentParts = parts, Content = ContentPartsEnvelope.FlattenText(parts), VisualArtifactIds = null };
        }).ToList();
    }

    private int FindBoundary(IReadOnlyList<ChatMessage> messages)
    {
        if (string.IsNullOrEmpty(currentInputHash)) return -1;
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Role == ChatRole.User && messages[i].SourceContentHash == currentInputHash)
                return i;
        return -1;
    }

    private static bool HasImages(ChatMessage message) => ChatMessageMultimodalNormalizer.GetImageParts(message).Count > 0;
}
