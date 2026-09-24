using System.Diagnostics;
using PuddingFullTextIndex;

namespace PuddingRetrievalEvalProbe;

/// <summary>
/// Read-only corpus inventory: how many files the full-text surface would index over a scope, and which
/// top-level directories dominate. It is raw evidence for the U4-1 ignore contract (which directories
/// actually cost us), and it sizes the indexing job before that job is started.
/// <para>
/// <b>Deliberate difference from the engine:</b> this uses a <b>single</b> filesystem walk and filters by
/// extension, whereas <c>LuceneSearchEngine.BuildIndexAsync</c> walks the tree <b>once per extension
/// pattern</b> (≈90 patterns in <c>FullTextIndexOptions</c>). Membership is identical because the very
/// same predicates (<c>IsIndexableExtension</c>, <c>IsExcludedPath</c>, size limits) are applied; only the
/// traversal is cheaper. The engine's per-extension walk is itself recorded as an observation for U4-1.
/// </para>
/// </summary>
internal static class CorpusInventory
{
    public static int Run(string scope, FullTextIndexOptions options, int top)
    {
        var perTopDirectory = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var perExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        long files = 0, bytes = 0;
        long visited = 0, notIndexable = 0, excluded = 0, skippedLarge = 0, skippedEmpty = 0, readErrors = 0;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            foreach (var file in Directory.EnumerateFiles(scope, "*", SearchOption.AllDirectories))
            {
                visited++;

                var extension = Path.GetExtension(file);
                if (!options.IsIndexableExtension(extension))
                {
                    notIndexable++;
                    continue;
                }

                if (options.IsExcludedPath(file, scope))
                {
                    excluded++;
                    continue;
                }

                long length;
                try
                {
                    length = new FileInfo(file).Length;
                }
                catch (Exception)
                {
                    readErrors++;
                    continue;
                }

                if (length > options.MaxFileSizeBytes) { skippedLarge++; continue; }
                if (length == 0) { skippedEmpty++; continue; }

                files++;
                bytes += length;

                var relative = Path.GetRelativePath(scope, file);
                var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
                var topDirectory = separator < 0 ? "<root>" : relative[..separator];

                perTopDirectory[topDirectory] = perTopDirectory.GetValueOrDefault(topDirectory) + 1;
                var key = extension.ToLowerInvariant();
                perExtension[key] = perExtension.GetValueOrDefault(key) + 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN  walk aborted: {ex.GetType().Name}: {ex.Message}");
        }

        stopwatch.Stop();

        Console.WriteLine($"COUNT  indexableFiles = {files}");
        Console.WriteLine($"COUNT  indexableBytes = {bytes}");
        Console.WriteLine($"COUNT  visitedFiles   = {visited}");
        Console.WriteLine($"COUNT  notIndexableExt= {notIndexable}");
        Console.WriteLine($"COUNT  excludedByOpts = {excluded}");
        Console.WriteLine($"COUNT  skippedLarge   = {skippedLarge}");
        Console.WriteLine($"COUNT  skippedEmpty   = {skippedEmpty}");
        Console.WriteLine($"COUNT  readErrors     = {readErrors}");
        Console.WriteLine($"COUNT  walkMs         = {stopwatch.ElapsedMilliseconds}");
        Console.WriteLine();
        Console.WriteLine($"Top {top} top-level directories by indexable file count (engine predicates applied):");

        var rank = 0;
        foreach (var entry in perTopDirectory.OrderByDescending(pair => pair.Value))
        {
            Console.WriteLine($"  {++rank,3}. {entry.Key,-32} {entry.Value,8}");
            if (rank >= top)
                break;
        }

        Console.WriteLine();
        Console.WriteLine("Indexable files by extension (top 20):");
        rank = 0;
        foreach (var entry in perExtension.OrderByDescending(pair => pair.Value))
        {
            Console.WriteLine($"  {++rank,3}. {entry.Key,-16} {entry.Value,8}");
            if (rank >= 20)
                break;
        }

        return 0;
    }
}
