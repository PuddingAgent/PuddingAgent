using PuddingCode.Configuration;

namespace PuddingCode.Abstractions;

/// <summary>
/// Manages important/pinned memory file for an agent instance (L4-PINNED layer).
/// File-based, no EF/DB dependency.
/// </summary>
public interface IImportantMemoryService
{
    public const int MaxLines = 100;
    public const int MaxChars = 1000;

    string? ReadOrNull(string agentInstanceId);
    Task<string?> ReadAsync(string agentInstanceId, CancellationToken ct = default);
    Task<bool> EnsureInitializedAsync(string agentInstanceId, CancellationToken ct = default);
    Task<ImportantMemoryWriteResult> WriteAsync(string agentInstanceId, string content, CancellationToken ct = default);
}

public sealed record ImportantMemoryWriteResult
{
    public bool Success { get; init; }
    public int LineCount { get; init; }
    public int CharCount { get; init; }
    public int ByteCount { get; init; }
    public string? Error { get; init; }
}
