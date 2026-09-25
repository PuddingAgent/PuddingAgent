using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using PuddingCodeIndex.Contracts;

namespace PuddingCodeIndex.Services;

/// <summary>
/// Aggregate <see cref="ICodeIndexer"/>: it runs <b>every</b> registered <see cref="ILanguageCodeIndexer"/>
/// over one scope and merges the results, so a scope is indexed in all of its languages instead of only in
/// the language whose implementation happened to be the registered <see cref="ICodeIndexer"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each implementation keeps filtering the workspace by its own supported extensions, so the <b>same</b>
/// descriptor is handed to all of them — no scope splitting is needed and none is done.
/// </para>
/// <para>
/// <b>A scope run succeeds when at least one language succeeded.</b> The scheduler derives the scope status
/// straight from <see cref="CodeIndexResult.Success"/>, and the absence of an optional toolchain (no Node.js
/// installed, for instance) is an <i>environment</i> fact: it must not turn a scope that C# indexed fine into
/// <c>Failed</c>. A run in which <b>all</b> languages fail still fails, so a genuinely broken configuration
/// stays visible.
/// </para>
/// <para>
/// Removal is the opposite case: rows a language was never asked to delete are stale data, so
/// <see cref="RemoveWorkspaceIndexAsync"/> requires <b>all</b> languages to succeed.
/// </para>
/// </remarks>
public sealed class CompositeCodeIndexer : ICodeIndexer, ICodeIndexFileUpdater
{
    /// <summary>Separator of the per-language messages merged into <see cref="CodeIndexResult.Message"/>.</summary>
    internal const string MessageSeparator = " | ";

    private readonly IReadOnlyList<ILanguageCodeIndexer> _indexers;
    private readonly ILogger<CompositeCodeIndexer>? _logger;

    /// <summary>Creates the aggregate over the registered language implementations.</summary>
    /// <param name="indexers">
    /// The language implementations, in merge order — the composition root registers them C# →
    /// TypeScript/JavaScript → Python. The order is data, not a constant: this type never names a language,
    /// so adding one is a registration-only change.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public CompositeCodeIndexer(
        IEnumerable<ILanguageCodeIndexer> indexers,
        ILogger<CompositeCodeIndexer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(indexers);

        _indexers = indexers.ToArray();
        _logger = logger;
    }

    /// <summary>The language implementations this aggregate fans out to, in merge order.</summary>
    public IReadOnlyList<ILanguageCodeIndexer> LanguageIndexers => _indexers;

    /// <inheritdoc />
    public async Task<CodeIndexResult> IndexWorkspaceAsync(
        CodeWorkspaceDescriptor workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var startedAt = DateTimeOffset.UtcNow;
        var outcomes = await RunAllAsync(
            (indexer, ct) => indexer.IndexWorkspaceAsync(workspace, ct),
            cancellationToken).ConfigureAwait(false);

        // At least one language succeeded — see the type remarks (red line: an unavailable optional language
        // must not fail a scope that another language indexed).
        var success = outcomes.Any(outcome => outcome.Success);

        return new CodeIndexResult(
            success,
            success ? CodeIndexStatus.Completed : CodeIndexStatus.Failed,
            MergeMessages(outcomes),
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            WorkspaceId: workspace.WorkspaceId,
            ProjectId: workspace.ProjectId,
            LanguageOutcomes: outcomes);
    }

    /// <inheritdoc />
    public async Task<CodeIndexResult> RemoveWorkspaceIndexAsync(
        string workspaceId,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var outcomes = await RunAllAsync(
            (indexer, ct) => indexer.RemoveWorkspaceIndexAsync(workspaceId, projectId, ct),
            cancellationToken).ConfigureAwait(false);

        // All-or-nothing: a language that failed leaves its rows behind.
        var failures = outcomes.Where(outcome => !outcome.Success).ToArray();
        var success = outcomes.Count > 0 && failures.Length == 0;

        var message = failures.Length > 0
            ? string.Join(
                MessageSeparator,
                failures.Select(failure => $"{failure.Language}: {failure.Message ?? "removal failed"}"))
            : outcomes.Select(outcome => outcome.Message).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));

        return new CodeIndexResult(
            success,
            success ? CodeIndexStatus.Completed : CodeIndexStatus.Failed,
            message,
            StartedAtUtc: startedAt,
            CompletedAtUtc: DateTimeOffset.UtcNow,
            WorkspaceId: workspaceId,
            ProjectId: projectId,
            LanguageOutcomes: outcomes);
    }

    /// <inheritdoc />
    public Task<CodeIndexResult> IndexFileAsync(
        CodeWorkspaceDescriptor workspace,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var owner = FindOwner(filePath);

        if (owner is null)
        {
            // Failed on purpose: the caller escalates a file that no language can handle to a scope-level
            // run, so a change is never silently dropped — the same rule the language implementations use.
            return Task.FromResult(new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                $"No language code indexer owns the file: {filePath}",
                WorkspaceId: workspace.WorkspaceId,
                ProjectId: workspace.ProjectId));
        }

        if (owner is not ICodeIndexFileUpdater updater)
        {
            // The owner of this extension has no per-file capability. The same rule the maintenance driver
            // applies to a registered indexer without one: report Failed so the caller escalates to a
            // scope-level run. The per-file port is deliberately separate from ICodeIndexer, so a language
            // is not required to support it.
            return Task.FromResult(new CodeIndexResult(
                false,
                CodeIndexStatus.Failed,
                $"The owning language indexer ({owner.Language}) has no per-file capability: {filePath}",
                WorkspaceId: workspace.WorkspaceId,
                ProjectId: workspace.ProjectId));
        }

        // The owner's result is returned unchanged — including its Failed, whose message is the owner's own
        // diagnosis ("Node.js not available", "File does not exist", …). Nothing is merged at this level.
        return updater.IndexFileAsync(workspace, filePath, cancellationToken);
    }

    /// <summary>Finds the single language implementation that owns <paramref name="filePath"/>.</summary>
    private ILanguageCodeIndexer? FindOwner(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(extension))
            return null;

        return _indexers.FirstOrDefault(indexer =>
            indexer.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs every language implementation in merge order and attributes each result to its language. A
    /// language that throws is contained: it is logged, reported as that language's failure, and the
    /// remaining languages still run.
    /// </summary>
    private async Task<IReadOnlyList<LanguageIndexOutcome>> RunAllAsync(
        Func<ILanguageCodeIndexer, CancellationToken, Task<CodeIndexResult>> call,
        CancellationToken cancellationToken)
    {
        var outcomes = new List<LanguageIndexOutcome>(_indexers.Count);

        foreach (var indexer in _indexers)
        {
            CodeIndexResult result;

            try
            {
                result = await call(indexer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller cancelled the whole run: not a language failure.
                throw;
            }
            catch (Exception ex)
            {
                // One language blowing up must not take the other languages (and the scope) down with it.
                _logger?.LogError(ex, "Language indexer {Language} threw.", indexer.Language);
                result = new CodeIndexResult(
                    false,
                    CodeIndexStatus.Failed,
                    $"{indexer.Language} indexer threw: {ex.Message}");
            }

            outcomes.Add(new LanguageIndexOutcome(indexer.Language, result.Success, result.Status, result.Message));
        }

        return outcomes;
    }

    /// <summary>
    /// Merges the per-language messages in fan-out order: the first language's message stays first, so a run
    /// under a single language reproduces its message byte for byte.
    /// </summary>
    private static string? MergeMessages(IReadOnlyList<LanguageIndexOutcome> outcomes)
    {
        var messages = outcomes
            .Select(outcome => outcome.Message)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToArray();

        return messages.Length == 0 ? null : string.Join(MessageSeparator, messages);
    }
}
