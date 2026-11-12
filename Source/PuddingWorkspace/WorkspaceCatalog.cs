using System.Collections.Immutable;
using PuddingWorkspace.Contracts;

namespace PuddingWorkspace;

/// <summary>
/// PA-05F authorized workspace surface: listing/environment expose logical info only, file operations run the
/// path policy first, links/reparse points are rejected before IO, writes are CAS on the expected content hash
/// and require an atomic adapter receipt. It never reads environment variables, LLM configuration or tokens.
/// </summary>
public sealed class WorkspaceCatalog
{
    private readonly IWorkspaceCatalogSource _catalog;
    private readonly IWorkspaceFileStore _files;

    public WorkspaceCatalog(IWorkspaceCatalogSource catalog, IWorkspaceFileStore files)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _files = files ?? throw new ArgumentNullException(nameof(files));
    }

    public async ValueTask<WorkspacePage> ListAsync(string? cursor, int maxCount, CancellationToken ct = default)
    {
        if (maxCount is < 1 or > 100) throw Error(WorkspaceErrorCodes.RequestInvalid, "The page size is out of bounds.");
        if (cursor is { Length: > 128 }) throw Error(WorkspaceErrorCodes.RequestInvalid, "The cursor is out of bounds.");
        WorkspacePage page;
        try
        {
            page = await _catalog.ListAsync(cursor, maxCount, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception) { throw Error(WorkspaceErrorCodes.SourceUnavailable, "The workspace catalog is unavailable."); }
        return new(page.Workspaces.Select(ToPublic).ToImmutableArray(), page.Cursor, page.Complete);
    }

    public async ValueTask<WorkspaceEnvironmentSnapshot> GetEnvironmentAsync(string workspaceId,
        CancellationToken ct = default)
    {
        WorkspaceFilePolicy.EnsureWorkspaceId(workspaceId);
        WorkspaceEnvironmentSnapshot? snapshot;
        try
        {
            snapshot = await _catalog.GetEnvironmentAsync(workspaceId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception) { throw Error(WorkspaceErrorCodes.SourceUnavailable, "The workspace catalog is unavailable."); }
        return snapshot ?? throw Error(WorkspaceErrorCodes.NotFound, "The workspace is not visible to this caller.");
    }

    public async ValueTask<WorkspaceFileContent> ReadFileAsync(string workspaceId, string relativePath,
        CancellationToken ct = default)
    {
        WorkspaceFilePolicy.EnsureWorkspaceId(workspaceId);
        var path = WorkspaceFilePolicy.Normalize(relativePath);
        if (await IsLinkAsync(workspaceId, path, ct).ConfigureAwait(false))
            throw Error(WorkspaceErrorCodes.LinkRejected, "Reading through a link/reparse point is not allowed.");
        var content = await _files.ReadAsync(workspaceId, path, ct).ConfigureAwait(false);
        return content ?? throw Error(WorkspaceErrorCodes.NotFound, "The file is not found in this workspace.");
    }

    public async ValueTask<WorkspaceWriteReceipt> WriteFileAsync(string workspaceId, string relativePath,
        string expectedContentHash, ImmutableArray<byte> content, CancellationToken ct = default)
    {
        WorkspaceFilePolicy.EnsureWorkspaceId(workspaceId);
        var path = WorkspaceFilePolicy.Normalize(relativePath);
        if (string.IsNullOrWhiteSpace(expectedContentHash) || expectedContentHash.Length > 128)
            throw Error(WorkspaceErrorCodes.RequestInvalid, "An expected content hash is required for a CAS write.");
        if (content.IsDefaultOrEmpty || content.Length > 1_048_576)
            throw Error(WorkspaceErrorCodes.RequestInvalid, "A bounded non-empty content payload is required.");
        if (await IsLinkAsync(workspaceId, path, ct).ConfigureAwait(false))
            throw Error(WorkspaceErrorCodes.LinkRejected, "Writing through a link/reparse point is not allowed.");
        var receipt = await _files.WriteAsync(workspaceId, path, expectedContentHash, content, ct).ConfigureAwait(false);
        if (!receipt.Atomic)
            throw Error(WorkspaceErrorCodes.Unsupported, "The adapter could not prove an atomic write.");
        return receipt;
    }

    private async ValueTask<bool> IsLinkAsync(string workspaceId, string path, CancellationToken ct)
    {
        try
        {
            return await _files.IsLinkAsync(workspaceId, path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (WorkspaceException) { throw; }
        catch (Exception) { throw Error(WorkspaceErrorCodes.Unsupported, "The adapter cannot verify link safety."); }
    }

    private static WorkspaceEntry ToPublic(WorkspaceEntry entry) => entry;

    private static WorkspaceException Error(string code, string message) => new(code, message);
}
