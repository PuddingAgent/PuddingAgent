using System.Diagnostics;
using PuddingRetrievalEval.Contracts;
using PuddingFullTextIndex.Contracts;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Real retrieval surface #1: the Lucene-backed full-text engine that backs the
/// <c>search_grep</c> fast path (<c>SearchGrepTool</c> holds an <see cref="IFullTextSearchEngine"/> and
/// asks it for candidate files before falling back to the managed scan).
/// <para>
/// The adapter is intentionally thin: it maps <see cref="SearchProbeRequest"/> onto the engine API and
/// times the <b>whole</b> call, as the port contract requires. It adds no filtering, no ranking and no
/// fallback of its own — anything the harness added here would be harness behaviour masquerading as
/// engine behaviour.
/// </para>
/// </summary>
internal sealed class LuceneFullTextProbe : ISearchProbe
{
    private readonly IFullTextSearchEngine _engine;

    public LuceneFullTextProbe(IFullTextSearchEngine engine, string name = "lucene-fulltext")
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Name = name;
    }

    public string Name { get; }

    public async Task<SearchProbeOutcome> SearchAsync(
        SearchProbeRequest request,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var extensionFilter = request.Scope.FileExtensions is { Count: > 0 }
                ? string.Join(";", request.Scope.FileExtensions)
                : null;

            var result = await _engine.SearchAsync(
                request.Query,
                request.Scope.RootDirectory,
                request.MaxResults,
                fileExtensionFilter: extensionFilter,
                subDirectoryFilter: null,
                ct: cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();

            if (!result.Success)
                return SearchProbeOutcome.Failure(stopwatch.Elapsed.TotalMilliseconds, result.Error ?? "engine reported failure");

            var hits = (result.Matches ?? [])
                .Select(match => new SearchProbeHit(match.FilePath, null, match.LineNumber))
                .ToArray();

            return new SearchProbeOutcome(hits, stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return SearchProbeOutcome.Failure(
                stopwatch.Elapsed.TotalMilliseconds,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
