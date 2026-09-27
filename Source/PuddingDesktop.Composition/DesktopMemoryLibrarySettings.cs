using Microsoft.Extensions.DependencyInjection;
using PuddingCode.Abstractions;
using PuddingDesktop.Foundation;
using PuddingPlatform.Services;

namespace PuddingDesktop.Composition;

/// <summary>
/// DS-08 library slice: binds the memory library cards to the shared MemoryLibraryAdminService. Every call
/// is workspace + agent scoped, so the adapter always passes both through.
/// </summary>
internal sealed class DesktopMemoryLibrarySettings(IDesktopKernel kernel) : IMemoryLibrarySettings
{
    private Task<T> Memory<T>(string operationId,
        Func<IMemoryLibraryAdminService, CancellationToken, Task<T>> body, CancellationToken cancellationToken)
        // Core registers the interface, not the concrete class, so the adapter must ask for the interface too.
        => kernel.RunSettingsAsync(operationId,
            (scope, token) => body(scope.Services.GetRequiredService<IMemoryLibraryAdminService>(), token),
            cancellationToken);

    public Task<IReadOnlyList<MemoryLibrary>> ListLibrariesAsync(
        string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Memory("memory.libraries.list", async (service, token) =>
        {
            var libraries = await service.GetLibrariesAsync(workspaceId, agentId, token);
            return (IReadOnlyList<MemoryLibrary>)libraries.Select(Map).ToArray();
        }, cancellationToken);

    public Task<MemoryLibrary> EnsureDefaultLibraryAsync(
        string workspaceId, string agentId, CancellationToken cancellationToken = default)
        => Memory("memory.libraries.ensureDefault", async (service, token) =>
        {
            return Map(await service.EnsureDefaultLibraryAsync(workspaceId, agentId, token));
        }, cancellationToken);

    public Task<IReadOnlyList<MemoryTreeNode>> ReadTreeAsync(
        string workspaceId, string agentId, string libraryId, CancellationToken cancellationToken = default)
        => Memory("memory.tree.read", async (service, token) =>
        {
            var nodes = await service.GetTreeAsync(workspaceId, agentId, libraryId, token);
            return (IReadOnlyList<MemoryTreeNode>)nodes.Select(Map).ToArray();
        }, cancellationToken);

    public Task CreateTreeNodeAsync(MemoryTreeNodeCreate create, CancellationToken cancellationToken = default)
        => Memory("memory.tree.create", async (service, token) =>
        {
            await service.CreateTreeNodeAsync(create.WorkspaceId, create.AgentId, new CreateMemoryTreeNodeRequest(
                create.WorkspaceId, create.LibraryId, Nullable(create.ParentNodeId), create.Name.Trim(),
                Nullable(create.Summary), create.NodeType), token);
            return true;
        }, cancellationToken);

    public Task<MemoryBook?> ReadBookAsync(
        string workspaceId, string agentId, string bookId, CancellationToken cancellationToken = default)
        => Memory("memory.book.read", async (service, token) =>
        {
            var book = await service.GetBookPageAsync(workspaceId, agentId, bookId, token);
            return book is null ? null : Map(book);
        }, cancellationToken);

    public Task CreateBookAsync(MemoryBookCreate create, CancellationToken cancellationToken = default)
        => Memory("memory.book.create", async (service, token) =>
        {
            await service.CreateBookAsync(create.WorkspaceId, create.AgentId, new CreateMemoryBookRequest(
                create.WorkspaceId, create.LibraryId, Nullable(create.NodeId), create.Title.Trim(),
                Nullable(create.Summary)), token);
            return true;
        }, cancellationToken);

    public Task UpdateBookAsync(MemoryBookEdit edit, CancellationToken cancellationToken = default)
        => Memory("memory.book.update", async (service, token) =>
        {
            await service.UpdateBookAsync(edit.WorkspaceId, edit.AgentId, edit.BookId,
                new UpdateMemoryBookRequest(edit.Title.Trim(), Nullable(edit.Summary)), token);
            return true;
        }, cancellationToken);

    public Task CreateChapterAsync(MemoryChapterCreate create, CancellationToken cancellationToken = default)
        => Memory("memory.chapter.create", async (service, token) =>
        {
            await service.CreateChapterAsync(create.WorkspaceId, create.AgentId, new CreateMemoryChapterRequest(
                create.BookId, create.Title.Trim(), create.Content, create.Importance), token);
            return true;
        }, cancellationToken);

    public Task UpdateChapterAsync(MemoryChapterEdit edit, CancellationToken cancellationToken = default)
        => Memory("memory.chapter.update", async (service, token) =>
        {
            await service.UpdateChapterAsync(edit.WorkspaceId, edit.AgentId, edit.ChapterId,
                new UpdateMemoryChapterRequest(edit.Title.Trim(), edit.Content, edit.Importance), token);
            return true;
        }, cancellationToken);

    public Task ArchiveBookAsync(string workspaceId, string agentId, string bookId, CancellationToken cancellationToken = default)
        => Memory("memory.book.archive", async (service, token) =>
        {
            if (!await service.ArchiveBookAsync(workspaceId, agentId, bookId, token))
                throw new InvalidOperationException($"Book '{bookId}' 未归档：Core 返回了否。");
            return true;
        }, cancellationToken);

    public Task ArchiveChapterAsync(string workspaceId, string agentId, string chapterId, CancellationToken cancellationToken = default)
        => Memory("memory.chapter.archive", async (service, token) =>
        {
            if (!await service.ArchiveChapterAsync(workspaceId, agentId, chapterId, token))
                throw new InvalidOperationException($"章节 '{chapterId}' 未归档：Core 返回了否。");
            return true;
        }, cancellationToken);

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static MemoryLibrary Map(LibraryRecord library) => new(
        library.LibraryId, library.WorkspaceId, library.Name, library.Description ?? "", library.AgentId ?? "",
        DateTimeOffset.FromUnixTimeMilliseconds(library.CreatedAt),
        DateTimeOffset.FromUnixTimeMilliseconds(library.UpdatedAt));

    private static MemoryTreeNode Map(MemoryLibraryTreeNodeDto node) => new(
        node.Id, node.ParentId ?? "", node.Type, node.Title, node.Summary ?? "", node.Status, node.BookId ?? "",
        node.Children?.Select(Map).ToArray() ?? []);

    private static MemoryBook Map(MemoryBookPageDto book) => new(
        book.WorkspaceId, book.LibraryId, book.BookId, book.Title, book.Summary ?? "", book.Status,
        book.Chapters.Select(chapter => new MemoryChapter(chapter.ChapterId, chapter.BookId, chapter.Title,
            chapter.Content, chapter.ContentType, chapter.Importance,
            DateTimeOffset.FromUnixTimeMilliseconds(chapter.CreatedAt),
            DateTimeOffset.FromUnixTimeMilliseconds(chapter.UpdatedAt))).ToArray());
}
