using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIndex.Contracts;

/// <summary>
/// Optional <b>per-file increment</b> capability of a code indexer (U3-B3).
/// <para>
/// Deliberately a separate port instead of a member of <see cref="ICodeIndexer"/>: an indexer is not
/// required to support per-file work, and adding a member to the full-workspace port breaks <b>every</b>
/// implementer — measured on 2026-09-24, the member version broke
/// <c>Tests/PuddingHost.Tests/Hosting/CodeIndexMaintenanceHostCompositionTests.cs</c> (a test double outside
/// this slice's write scope). Keeping the capability explicit means a caller can ask for it and degrade
/// safely instead of the contract silently promising something an implementation does not do.
/// </para>
/// </summary>
public interface ICodeIndexFileUpdater
{
    /// <summary>
    /// Indexes a single file of a scope, replacing the rows the index currently holds for it, instead of
    /// re-reading every file of the scope.
    /// <para>
    /// Implementations must either do the whole per-file replacement or report
    /// <see cref="CodeIndexStatus.Failed"/> with a reason: the caller then escalates to a scope-level run,
    /// so a change is never silently dropped because a file could not be handled incrementally. A file that
    /// no longer exists, that is not a source file of this indexer, or that is excluded from indexing is
    /// <b>not</b> a success — it is reported as failed so the caller keeps control.
    /// </para>
    /// </summary>
    /// <param name="workspace">Workspace descriptor that owns the file.</param>
    /// <param name="filePath">Absolute path of the file to index.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    Task<CodeIndexResult> IndexFileAsync(
        CodeWorkspaceDescriptor workspace,
        string filePath,
        CancellationToken cancellationToken = default);
}
