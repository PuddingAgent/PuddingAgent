using System.Collections.Immutable;

namespace PuddingTools.Contracts;

// PA-05B 工具门面端口（BCL-only）：只提供搜索/检查/状态读取，不提供执行入口。
// 不可见工具不得在搜索/检查/状态中泄露存在性；未知/不可用状态原样保留，不伪造"可用"。

public enum ToolSideEffect { None, Read, Write, Network, Process }

public sealed record ToolDescriptor(string ToolId, string Name, string Category, string Description,
    ToolSideEffect SideEffect, bool Visible, long Revision)
{
    public bool IsValid => Valid(ToolId) && Valid(Name) && Valid(Category) && Description.Length <= 4096;

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
}

public sealed record ToolQuery(string? Text, string? Category, ToolSideEffect? SideEffect, string? Cursor,
    int MaxCount);

public sealed record ToolSearchPage(ImmutableArray<ToolDescriptor> Tools, string? Cursor, bool Complete);

public sealed record ToolSchema(string ToolId, string InputSchemaJson, string OutputSchemaJson, string? Help,
    long Revision);

/// <summary>Availability is a string so adapters can surface an explicit unknown instead of a false "available".</summary>
public sealed record ToolState(string ToolId, string Availability, string? MissingReason, long Revision);

public interface IToolCatalogSource
{
    ValueTask<ToolSearchPage> SearchAsync(ToolQuery query, CancellationToken ct = default);
    ValueTask<ToolSchema?> GetSchemaAsync(string toolId, CancellationToken ct = default);
    ValueTask<ToolState?> GetStateAsync(string toolId, CancellationToken ct = default);
}

public static class ToolErrorCodes
{
    public const string QueryInvalid = "tool.query_invalid";
    public const string ToolInvalid = "tool.id_invalid";
    public const string NotFound = "tool.not_found";
    public const string SourceUnavailable = "tool.source_unavailable";
}

public sealed class ToolFacadeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
