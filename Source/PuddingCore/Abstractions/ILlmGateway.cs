using PuddingCode.Models;

namespace PuddingCode.Abstractions;

/// <summary>
/// LLM API 网关。屏蔽 Chat Completions、Responses 和 Anthropic Messages
/// 等模型级 wire protocol 差异。
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
