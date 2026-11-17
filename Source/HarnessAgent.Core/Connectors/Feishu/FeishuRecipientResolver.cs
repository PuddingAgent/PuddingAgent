namespace HarnessAgent.Core.Connectors.Feishu;

/// <summary>
/// 兜底收件人的判定：显式 <c>open_id</c> 优先，否则回落到 <c>chat_id</c>。
///
/// <para>
/// 飞书发消息必须成对给出「接收方 ID」与 <c>receive_id_type</c>。把 open_id 当成
/// chat_id 提交，或反过来，都会被服务端拒绝。
/// </para>
///
/// <para>
/// 这里收拢成一处，是因为它曾经在四条发送路径（文本 / 语音 / 图片 / 文件）里各写了一遍，
/// 而只有文本那条读 <c>metadata["open_id"]</c>：派发器在显式收件人场景会写入
/// <c>open_id</c> 并主动删掉 <c>chat_id</c> / <c>message_id</c>，于是语音、图片、文件
/// 三条路会把 open_id 当成 chat_id 发出去。收拢后新增发送路径无法再各自漏读。
/// </para>
/// </summary>
public static class FeishuRecipientResolver
{
    /// <summary>显式收件人 id 在消息元数据里的键（由派发器写入）。</summary>
    public const string OpenIdMetadataKey = "open_id";

    /// <summary>按 open_id 发送时的 <c>receive_id_type</c> 取值。</summary>
    public const string OpenIdReceiveIdType = "open_id";

    /// <summary>按群聊 chat_id 发送时的 <c>receive_id_type</c> 取值。</summary>
    public const string ChatIdReceiveIdType = "chat_id";

    /// <summary>
    /// 解析兜底收件人与对应的 <c>receive_id_type</c>。
    /// </summary>
    /// <param name="metadata">消息元数据；含显式 <c>open_id</c> 时按其发送。</param>
    /// <param name="target">没有显式 open_id 时的目标，此时视为群聊 chat_id。</param>
    /// <returns>接收方 ID 与其类型，两者必须成对使用。</returns>
    /// <exception cref="ArgumentException">两者都没有可用值。</exception>
    public static (string Target, string ReceiveIdType) Resolve(
        IReadOnlyDictionary<string, string>? metadata,
        string? target)
    {
        if (metadata is not null
            && metadata.TryGetValue(OpenIdMetadataKey, out var openId)
            && !string.IsNullOrWhiteSpace(openId))
        {
            return (openId, OpenIdReceiveIdType);
        }

        if (!string.IsNullOrWhiteSpace(target))
            return (target, ChatIdReceiveIdType);

        throw new ArgumentException(
            "No open_id or chat target specified for Feishu message");
    }
}
