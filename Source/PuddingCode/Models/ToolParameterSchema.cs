namespace PuddingCode.Models;

/// <summary>描述工具参数的 JSON Schema</summary>
public sealed record ToolParameterSchema(
    IReadOnlyList<ToolParameter> Properties,
    IReadOnlyList<string> Required);

public sealed record ToolParameter(
    string Name,
    string Type,
    string Description);
