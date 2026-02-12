using PuddingCode.Abstractions;

namespace PuddingRuntime.Services;

public sealed record WikiPageWriteRequest
{
    public required string WorkspaceId { get; init; }
    public string? LibraryId { get; init; }
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
    public required MemoryWikiPageUpdatePlan Plan { get; init; }
}

public sealed record WikiPageWriteResult
{
    public required string BookId { get; init; }
    public required string BookTitle { get; init; }
    public required string PageId { get; init; }
    public required string PagePath { get; init; }
    public required bool CreatedBook { get; init; }
    public required bool CreatedPage { get; init; }
}

/// <summary>
/// Minimal deterministic write entry for Memory v2 V1.
/// It creates or finds Book/Page and replaces the page body with LLM-produced final content.
/// </summary>
public sealed class WikiPageWriteEntry
{
    private readonly IMemoryLibrary _library;

    public WikiPageWriteEntry(IMemoryLibrary library)
    {
        _library = library;
    }

    public async Task<IReadOnlyList<WikiPageWriteResult>> WriteAsync(
        WikiPageWriteRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.WorkspaceId))
            throw new ArgumentException("WorkspaceId is required.", nameof(request));
        if (request.Plan.Updates.Count == 0)
            return [];

        var library = await ResolveLibraryAsync(request.WorkspaceId, request.LibraryId, ct);
        var results = new List<WikiPageWriteResult>();

        foreach (var update in request.Plan.Updates)
        {
            var bookTitle = NormalizeBookTitle(update.Book);
            var pagePath = NormalizePagePath(update.Page);
            var book = await _library.FindBookByTitleAsync(library.LibraryId, bookTitle, ct);
            var createdBook = false;
            if (book is null)
            {
                book = await _library.CreateBookAsync(
                    library.LibraryId,
                    bookTitle,
                    BuildBookSummary(update.Content),
                    tagPaths: null,
                    ct);
                createdBook = true;
            }

            var chapters = await _library.ListChaptersAsync(book.BookId, ct);
            var page = chapters.FirstOrDefault(c =>
                string.Equals(NormalizePagePath(c.Title), pagePath, StringComparison.OrdinalIgnoreCase));

            var createdPage = false;
            if (page is null)
            {
                page = await _library.AddChapterAsync(
                    book.BookId,
                    pagePath,
                    update.Content,
                    chapterOrder: chapters.Count,
                    sourceSessionId: request.SessionId,
                    agentInstanceId: request.AgentId,
                    ct);
                createdPage = true;
            }
            else if (!string.Equals(page.Content, update.Content, StringComparison.Ordinal))
            {
                page = await _library.UpdateChapterContentAsync(page.ChapterId, update.Content, ct);
            }

            results.Add(new WikiPageWriteResult
            {
                BookId = book.BookId,
                BookTitle = book.Title,
                PageId = page.ChapterId,
                PagePath = pagePath,
                CreatedBook = createdBook,
                CreatedPage = createdPage,
            });
        }

        return results;
    }

    private async Task<LibraryRecord> ResolveLibraryAsync(
        string workspaceId,
        string? libraryId,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(libraryId))
        {
            var existing = await _library.GetLibraryAsync(libraryId, ct);
            if (existing is not null)
                return existing;
        }

        var libraries = await _library.ListLibrariesAsync(workspaceId, ct);
        return libraries.Count > 0
            ? libraries[0]
            : await _library.CreateLibraryAsync(workspaceId, "默认图书馆", null, ct);
    }

    private static string NormalizeBookTitle(string title)
    {
        var normalized = title.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Book title is required.");
        return normalized;
    }

    private static string NormalizePagePath(string page)
    {
        var normalized = page.Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Page path is required.");
        normalized = "/" + normalized.Trim('/');
        return normalized == "/" ? "/index" : normalized;
    }

    private static string BuildBookSummary(string content)
    {
        var normalized = content.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }
}
