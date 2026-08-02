using System.Text.Json;
using PuddingBrowser.Abstractions;
using PuddingCode.Tools;

namespace PuddingBrowser.AgentTools;

public sealed record BrowserToolError
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public sealed record BrowserToolResult<T>
{
    public required bool Ok { get; init; }
    public string? ContextId { get; init; }
    public string? PageId { get; init; }
    public long? PageVersion { get; init; }
    public T? Value { get; init; }
    public BrowserToolError? Error { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BrowserContextToolValue
{
    public required string ContextId { get; init; }
    public bool Persistent { get; init; }
    public int PageCount { get; init; }
}

public sealed record BrowserTabToolValue
{
    public required string ContextId { get; init; }
    public required string PageId { get; init; }
    public required string Title { get; init; }
    public required string Url { get; init; }
    public long PageVersion { get; init; }
}

public sealed record BrowserNavigationToolValue
{
    public required string Action { get; init; }
    public required BrowserTabToolValue Page { get; init; }
    public bool? NavigationOk { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorText { get; init; }
}

public sealed record BrowserSnapshotToolValue
{
    public string? DomText { get; init; }
    public string? AccessibilityTree { get; init; }
    public string? Html { get; init; }
    public bool Truncated { get; init; }
    public int NodeCount { get; init; }
}

public sealed record BrowserElementToolValue
{
    public required string Ref { get; init; }
    public required string Tag { get; init; }
    public string? Role { get; init; }
    public string? Name { get; init; }
    public string? Text { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool? Checked { get; init; }
    public BoundingBox? BoundingBox { get; init; }
}

public sealed record BrowserLocateToolValue
{
    public required int Count { get; init; }
    public required IReadOnlyList<BrowserElementToolValue> Elements { get; init; }
}

public sealed record BrowserInteractionToolValue
{
    public required string Action { get; init; }
    public required BrowserTabToolValue Page { get; init; }
    public BrowserElementToolValue? Element { get; init; }
}

public sealed record BrowserWaitToolValue
{
    public bool TimedOut { get; init; }
    public string? Error { get; init; }
    public required BrowserTabToolValue Page { get; init; }
}

internal static class BrowserToolResponse
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static ToolExecutionResult Success(
        object? value,
        BrowserContextId? contextId = null,
        PageId? pageId = null,
        long? pageVersion = null,
        IReadOnlyList<string>? warnings = null)
        => ToolExecutionResult.Ok(JsonSerializer.Serialize(new BrowserToolResult<object>
        {
            Ok = true,
            ContextId = contextId?.Value,
            PageId = pageId?.Value,
            PageVersion = pageVersion,
            Value = value,
            Warnings = warnings ?? []
        }, s_jsonOptions));

    public static ToolExecutionResult Failure(
        string code,
        string message,
        BrowserContextId? contextId = null,
        PageId? pageId = null)
        => ToolExecutionResult.Fail(JsonSerializer.Serialize(new BrowserToolResult<object>
        {
            Ok = false,
            ContextId = contextId?.Value,
            PageId = pageId?.Value,
            Error = new BrowserToolError { Code = code, Message = message }
        }, s_jsonOptions));

    public static ToolExecutionResult FromException(
        BrowserOperationException exception,
        BrowserContextId? contextId = null,
        PageId? pageId = null)
        => Failure(exception.Code, exception.Message, contextId, pageId);

    public static BrowserContextToolValue Context(BrowserContextInfo info) => new()
    {
        ContextId = info.Id.Value,
        Persistent = info.Persistent,
        PageCount = info.PageCount
    };

    public static BrowserTabToolValue Page(PageInfo info) => new()
    {
        ContextId = info.ContextId.Value,
        PageId = info.Id.Value,
        Title = info.Title,
        Url = info.Url,
        PageVersion = info.PageVersion
    };

    public static BrowserElementToolValue Element(BrowserElementInfo info) => new()
    {
        Ref = info.Ref,
        Tag = info.Tag,
        Role = info.Role,
        Name = info.Name,
        Text = info.Text,
        Visible = info.Visible,
        Enabled = info.Enabled,
        Checked = info.Checked,
        BoundingBox = info.BoundingBox
    };
}
