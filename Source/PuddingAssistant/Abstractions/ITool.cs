using PuddingAssistant.Models;

namespace PuddingAssistant.Abstractions;

/// <summary>
/// Agent 可调用的工具。实现此接口后注册到 ToolRegistry。
/// </summary>
public interface ITool
{
    /// <summary>工具名称，对应 LLM function calling 的 name 字段</summary>
    string Name { get; }

    /// <summary>工具描述，告诉 LLM 这个工具做什么</summary>
    string Description { get; }

    /// <summary>JSON Schema 格式的参数定义，用于生成 tools JSON</summary>
    ToolParameterSchema Parameters { get; }

    /// <summary>执行工具，传入 LLM 返回的参数 JSON，返回结果文本</summary>
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct = default);
}
