using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// LLM API 网关。屏蔽不同供应商的差异（Claude / GPT / DeepSeek），
/// 统一使用 OpenAI Chat Completions 兼容协议。
/// </summary>
public interface ILlmGateway
{
    /// <summary>发送对话消息，获取 LLM 响应（可能包含 tool_calls）</summary>
    Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default);

    /// <summary>
    /// 流式发送对话消息。每个 SSE chunk 作为一个 StreamDelta yield 出来。
    /// 调用方负责累积 delta 组装最终 LlmResponse。
    /// </summary>
    IAsyncEnumerable<StreamDelta> ChatStreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ITool> tools,
        CancellationToken ct = default);
}
