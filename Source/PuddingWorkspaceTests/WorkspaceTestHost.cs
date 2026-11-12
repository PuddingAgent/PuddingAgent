using System.Collections.Immutable;
using PuddingWorkspace.Contracts;

namespace PuddingWorkspaceTests;

public sealed class FakeWorkspaceSource : IWorkspaceCatalogSource
{
    public WorkspacePage Page { get; set; } = new([], null, true);
    public WorkspaceEnvironmentSnapshot? Environment { get; set; }
    public Exception? Failure { get; set; }
    public int Calls { get; private set; }

    public ValueTask<WorkspacePage> ListAsync(string? cursor, int maxCount, CancellationToken ct = default)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Page);
    }

    public ValueTask<WorkspaceEnvironmentSnapshot?> GetEnvironmentAsync(string workspaceId,
        CancellationToken ct = default)
    {
        Calls++;
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Environment);
    }
}

public sealed class FakeFileStore : IWorkspaceFileStore
{
    public WorkspaceFileContent? Content { get; set; }
    public bool Link { get; set; }
    public bool Atomic { get; set; } = true;
    public Exception? Failure { get; set; }
    public string? WrittenPath { get; private set; }
    public string? ExpectedHash { get; private set; }

    public ValueTask<WorkspaceFileContent?> ReadAsync(string workspaceId, string normalizedPath,
        CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Content);
    }

    public ValueTask<WorkspaceWriteReceipt> WriteAsync(string workspaceId, string normalizedPath,
        string expectedContentHash, ImmutableArray<byte> content, CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        WrittenPath = normalizedPath;
        ExpectedHash = expectedContentHash;
        return ValueTask.FromResult(new WorkspaceWriteReceipt(workspaceId, normalizedPath, 2, "hash-new",
            content.Length, Atomic));
    }

    public ValueTask<bool> IsLinkAsync(string workspaceId, string normalizedPath, CancellationToken ct = default)
    {
        if (Failure is not null) throw Failure;
        return ValueTask.FromResult(Link);
    }
}

internal static class WorkspaceFixtures
{
    public static WorkspaceEntry Entry(string id = "ws-1") => new(id, "Default", "default", "available", 3);
    public static WorkspaceEnvironmentSnapshot Environment(string workspaceId = "ws-1") => new(workspaceId,
        "workspace:default", "windows", ["files.read", "files.write"], "policy-v1");
    public static ImmutableArray<byte> Bytes(string text) => [.. System.Text.Encoding.UTF8.GetBytes(text)];
}
