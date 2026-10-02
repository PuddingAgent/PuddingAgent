using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;


[TestClass]
public sealed class IndependentMemoryProbe12
{
    [TestMethod]
    public async Task PreferenceWithoutValue_MustFailBeforeWriteAsAdvertisedBySchema()
    {
        var library = new RecordingMemoryLibraryConvenience();
        await ExecuteAsync(CreateTool(library), new Dictionary<string, object?> {
            ["action"]="upsert", ["type"]="preference", ["key"]="locale"
        });
        Assert.AreEqual(0, library.UpsertCalls, "Value is declared required in SaveMemoryArgs.");
    }
    [TestMethod]
    public async Task PreferenceTypeCase_MustNormalizeOrRejectBeforeWrite()
    {
        var library = new RecordingMemoryLibraryConvenience();
        await ExecuteAsync(CreateTool(library), new Dictionary<string, object?> {
            ["action"]="upsert", ["type"]="Preference", ["key"]="locale", ["value"]="zh-CN"
        });
        Assert.IsTrue(library.UpsertCalls == 0 || library.LastExperience?.Content == "locale: zh-CN",
            "Case-insensitive validation must not route to a blank-content generic write.");
    }
    private static SaveMemoryTool CreateTool(
        RecordingMemoryLibraryConvenience library,
        IImportantMemoryService? importantMemory = null)
        => new(library, null!, NullLogger<SaveMemoryTool>.Instance, importantMemory);

    private static async Task<ToolExecutionResult> ExecuteAsync<TArgs>(
        PuddingToolBase<TArgs> tool,
        IReadOnlyDictionary<string, object?> parameters,
        CancellationToken ct = default)
        where TArgs : class
    {
        return await tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-1",
            ArgumentsJson = JsonSerializer.Serialize(parameters),
            Context = new ToolExecutionContext
            {
                AgentInstanceId = "agent",
                WorkspaceId = "workspace",
                SessionId = "session",
            },
        }, ct);
    }

    /// <summary>只记录是否发生写入；命中即失败，用于证明 fail-closed 分支没有落到写路径。</summary>
    private sealed class RecordingMemoryLibraryConvenience : IMemoryLibraryConvenience
    {
        public int UpsertCalls { get; private set; }
        public ExperiencePackage? LastExperience { get; private set; }

        public bool ThrowCancellation { get; init; }

        public Task<ExperienceWriteResult> UpsertExperienceAsync(
            string workspaceId, ExperiencePackage experience, CancellationToken ct = default)
        {
            UpsertCalls++;
            LastExperience = experience;
            if (ThrowCancellation)
                throw new OperationCanceledException(ct);
            throw new NotSupportedException("UpsertExperienceAsync must not succeed in contract tests.");
        }

        public Task<IReadOnlyList<RankedResult>> SmartSearchAsync(
            string naturalLanguageQuery, int topK = 20, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<BookRecord> GetOrCreateBookAsync(
            string libraryId, string title, string? summary, IReadOnlyList<string>? tagPaths, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<ChapterRecord> AppendChapterAsync(
            string bookId, string title, string content, string? sourceSessionId = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PointerRecord>> AutoDiscoverPointersAsync(
            string chapterId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<TagTreeNode>> GetTagRootsAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task StartDeepExploreAsync(string query, CancellationToken ct = default)
            => throw new NotSupportedException();

        public IReadOnlyList<RankedResult> GetPendingExplorations(string query)
            => throw new NotSupportedException();
    }

    /// <summary>记录 important 读写所用的身份与内容，验证 context 派生合同。</summary>
    private sealed class RecordingImportantMemoryService : IImportantMemoryService
    {
        public string? LastWriteInstanceId { get; private set; }
        public string? LastWriteContent { get; private set; }
        public string? LastReadInstanceId { get; private set; }
        public string? Content { get; init; }

        public string? ReadOrNull(string agentInstanceId) => Content;

        public Task<string?> ReadAsync(string agentInstanceId, CancellationToken ct = default)
        {
            LastReadInstanceId = agentInstanceId;
            return Task.FromResult(Content);
        }

        public Task<bool> EnsureInitializedAsync(string agentInstanceId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<ImportantMemoryWriteResult> WriteAsync(
            string agentInstanceId, string content, CancellationToken ct = default)
        {
            LastWriteInstanceId = agentInstanceId;
            LastWriteContent = content;
            return Task.FromResult(new ImportantMemoryWriteResult
            {
                Success = true,
                LineCount = 1,
                CharCount = content.Length,
                ByteCount = System.Text.Encoding.UTF8.GetByteCount(content),
            });
        }
    }
}
