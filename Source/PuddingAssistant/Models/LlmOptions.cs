namespace PuddingAssistant.Models;

/// <summary>LLM 连接配置（OpenAI-compatible）</summary>
public sealed record LlmOptions(
    string Endpoint,
    string ApiKey,
    string Model,
    double? Temperature = null,
    int? MaxTokens = null);
