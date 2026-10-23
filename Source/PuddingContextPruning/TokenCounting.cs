using PuddingCode.Models;

namespace PuddingContextPruning;

/// <summary>
/// token 估算的唯一边界：组件只依赖这一个接口，宿主可以注入真实分词器而不必改本组件。
/// 组件自身绝不内置模型分词表（那会把「零依赖」和「确定性」一起破坏）。
/// </summary>
public interface ITextTokenCounter
{
    /// <summary>估算一段文本的 token 数；<c>null</c> 与空串都返回 0。</summary>
    int CountTokens(string? text);
}

/// <summary>
/// 字符比例估算：<c>max(1, ceil(length / CharsPerToken))</c>，默认 4 字符 ≈ 1 token。
/// <para>
/// 只用于「剪枝前后是否真的省了东西」的准入判定，绝不用于 provider 的真实计费口径。
/// </para>
/// </summary>
public sealed class CharacterRatioTokenCounter : ITextTokenCounter
{
    /// <summary>创建按固定字符比例估算的计数器。</summary>
    /// <param name="charsPerToken">每个 token 折算的字符数，必须 &gt; 0。</param>
    public CharacterRatioTokenCounter(int charsPerToken = 4)
    {
        if (charsPerToken <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(charsPerToken),
                charsPerToken,
                "charsPerToken 必须 > 0。");

        CharsPerToken = charsPerToken;
    }

    /// <summary>默认计数器（4 字符 / token）；无状态，可安全复用。</summary>
    public static CharacterRatioTokenCounter Default { get; } = new();

    /// <summary>每个 token 折算的字符数。</summary>
    public int CharsPerToken { get; }

    /// <inheritdoc />
    public int CountTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // 先转 long 再除：极长文本的 length + CharsPerToken - 1 在 int 上可能溢出。
        return (int)Math.Max(1L, ((long)text.Length + CharsPerToken - 1) / CharsPerToken);
    }
}

/// <summary>
/// 消息级 token 计量。只累计**会被 provider 看见的**字段：
/// 正文、推理正文、工具身份、工具参数、多模态 id、文本部件。
/// <para>
/// 明确不计入 <c>IsContextSummary</c>、<c>SourceContentHash</c>、<c>SourceMessageId</c>：
/// 它们不是 provider 字段，也不是可省的体积。
/// </para>
/// </summary>
public static class PruneTokenMath
{
    /// <summary>单条消息的估算 token 数。</summary>
    public static int CountMessage(ChatMessage message, ITextTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(counter);

        var tokens = counter.CountTokens(message.Content);
        tokens += counter.CountTokens(message.ReasoningContent);
        tokens += counter.CountTokens(message.ToolCallId);
        tokens += counter.CountTokens(message.ToolName);

        if (message.ToolCalls is { Count: > 0 } toolCalls)
        {
            foreach (var call in toolCalls)
            {
                tokens += counter.CountTokens(call.Id);
                tokens += counter.CountTokens(call.Name);
                tokens += counter.CountTokens(call.ArgumentsJson);
            }
        }

        tokens += CountIdentifiers(message.VisualArtifactIds, counter);
        tokens += CountIdentifiers(message.AudioArtifactIds, counter);

        if (message.ContentParts is { Count: > 0 } parts)
        {
            // 只有文本部件带正文；图片部件只带 artifact id，其字节由 provider 边界解析。
            foreach (var part in parts)
            {
                if (part is LlmTextPart text)
                    tokens += counter.CountTokens(text.Text);
            }
        }

        return tokens;
    }

    /// <summary>整份消息列表的估算 token 数（逐条求和）。</summary>
    public static int CountMessages(IReadOnlyList<ChatMessage> messages, ITextTokenCounter counter)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(counter);

        long total = 0;
        foreach (var message in messages)
            total += CountMessage(message, counter);

        return (int)Math.Min(total, int.MaxValue);
    }

    private static int CountIdentifiers(IReadOnlyList<string>? identifiers, ITextTokenCounter counter)
    {
        if (identifiers is not { Count: > 0 })
            return 0;

        var tokens = 0;
        foreach (var identifier in identifiers)
            tokens += counter.CountTokens(identifier);

        return tokens;
    }
}
