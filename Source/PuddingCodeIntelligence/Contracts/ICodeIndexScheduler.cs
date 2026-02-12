using System.Threading;
using System.Threading.Tasks;

namespace PuddingCodeIntelligence.Contracts;

/// <summary>
/// Enqueues background code indexing work. Ensure/register operations only
/// write the registry entry and fire-and-forget the indexing job through
/// this scheduler. Never blocks agent tool calls.
/// </summary>
public interface ICodeIndexScheduler
{
    /// <summary>
    /// Enqueue a scope for indexing. Returns immediately; indexing runs
    /// in the background.
    /// </summary>
    void Enqueue(string workspaceId, string scopeId);

    /// <summary>
    /// Get the current queue depth for a workspace.
    /// </summary>
    int GetQueueDepth(string workspaceId);

    /// <summary>
    /// Check whether a scope is currently being indexed.
    /// </summary>
    bool IsIndexing(string workspaceId, string scopeId);
}
