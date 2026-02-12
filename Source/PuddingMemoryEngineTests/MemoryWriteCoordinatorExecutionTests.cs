using PuddingCode.Models;
using PuddingRuntime.Services;

namespace PuddingMemoryEngineTests;

[TestClass]
public sealed class MemoryWriteCoordinatorExecutionTests
{
    [TestMethod]
    public async Task CoordinateAsync_ShouldExecuteAppendNewByCreatingChapter()
    {
        await using var scope = await MemoryLibraryTests.CreateLibraryScopeAsync();
        var coordinator = new MemoryWriteCoordinator(
            new MemoryWriteCommandValidator(),
            memoryLibrary: scope.Library);

        var envelope = await coordinator.CoordinateAsync(new MemoryWriteCommand
        {
            CommandId = "cmd-append-1",
            WorkspaceId = "workspace-1",
            Intent = MemoryWriteIntents.AppendNew,
            Mode = MemoryWriteExecutionModes.Execute,
            Source = new MemoryWriteSource
            {
                SourceKind = MemoryWriteSourceKinds.RuntimeTool,
                SessionId = "session-1",
            },
            Payload = new MemoryWritePayload
            {
                Title = "Preference",
                Content = "User prefers concise engineering summaries.",
                Confidence = 0.82,
            },
        });

        Assert.AreEqual(MemoryWriteResultStatuses.Executed, envelope.Status);
        Assert.AreEqual(MemoryWriteExecutionModes.Execute, envelope.Mode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(envelope.BookId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(envelope.ChapterId));

        var libraries = await scope.Library.ListLibrariesAsync("workspace-1");
        Assert.AreEqual(1, libraries.Count);

        var books = await scope.Library.ListBooksAsync(libraries[0].LibraryId);
        Assert.AreEqual(1, books.Count);
        Assert.AreEqual("Preference", books[0].Title);

        var chapters = await scope.Library.ListChaptersAsync(books[0].BookId);
        Assert.AreEqual(1, chapters.Count);
        Assert.AreEqual("Preference", chapters[0].Title);
        Assert.AreEqual("User prefers concise engineering summaries.", chapters[0].Content);
        Assert.AreEqual("session-1", chapters[0].SourceSessionId);
        Assert.AreEqual(chapters[0].ChapterId, envelope.ChapterId);
    }
}
