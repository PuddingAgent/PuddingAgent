using System.Collections.Immutable;

namespace PuddingWorkspace.Contracts;

// PA-05F 工作区端口（BCL-only）：只暴露受权逻辑信息；真实路径、OS 与持久存储由适配器实现。
// 环境快照不得 dump 环境变量/LLM 配置/控制令牌；文件写入带 expected hash 并由适配器原子落盘。

public sealed record WorkspaceEntry(string WorkspaceId, string DisplayName, string RootName, string Availability,
    long Revision);

public sealed record WorkspacePage(ImmutableArray<WorkspaceEntry> Workspaces, string? Cursor, bool Complete);

/// <summary>Authorized logical environment only: no environment variables, credentials or control tokens.</summary>
public sealed record WorkspaceEnvironmentSnapshot(string WorkspaceId, string LogicalRoot, string OsFamily,
    ImmutableArray<string> Capabilities, string PolicyVersion);

public sealed record WorkspaceFileContent(string WorkspaceId, string Path, long Revision, string ContentHash,
    ImmutableArray<byte> Content);

public sealed record WorkspaceWriteReceipt(string WorkspaceId, string Path, long Revision, string ContentHash,
    long Size, bool Atomic);

public interface IWorkspaceCatalogSource
{
    ValueTask<WorkspacePage> ListAsync(string? cursor, int maxCount, CancellationToken ct = default);
    ValueTask<WorkspaceEnvironmentSnapshot?> GetEnvironmentAsync(string workspaceId, CancellationToken ct = default);
}

/// <summary>Real filesystem adapter; it only receives an already-normalized, in-workspace relative path.</summary>
public interface IWorkspaceFileStore
{
    ValueTask<WorkspaceFileContent?> ReadAsync(string workspaceId, string normalizedPath, CancellationToken ct = default);
    /// <summary>CAS on the expected content hash; the adapter writes atomically and returns the new revision.</summary>
    ValueTask<WorkspaceWriteReceipt> WriteAsync(string workspaceId, string normalizedPath, string expectedContentHash,
        ImmutableArray<byte> content, CancellationToken ct = default);
    /// <summary>True when the final path (or a parent) is a symlink/junction/reparse point that could escape the root.</summary>
    ValueTask<bool> IsLinkAsync(string workspaceId, string normalizedPath, CancellationToken ct = default);
}

public static class WorkspaceErrorCodes
{
    public const string RequestInvalid = "workspace.request_invalid";
    public const string PathRejected = "workspace.path_rejected";
    public const string LinkRejected = "workspace.link_rejected";
    public const string WorkspaceForbidden = "workspace.forbidden";
    public const string NotFound = "workspace.not_found";
    public const string HashMismatch = "workspace.hash_mismatch";
    public const string Unsupported = "workspace.unsupported";
    public const string SourceUnavailable = "workspace.source_unavailable";
}

public sealed class WorkspaceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
