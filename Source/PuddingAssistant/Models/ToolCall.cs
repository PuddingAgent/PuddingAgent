namespace PuddingAssistant.Models;

/// <summary>工具调用请求</summary>
public sealed record ToolCall(string Id, string Name, string ArgumentsJson);
