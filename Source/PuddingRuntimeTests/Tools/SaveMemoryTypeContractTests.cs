using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// T01-R 回归：save_memory 的参数声明、校验与内容构造必须一致。
/// 1) preference 缺 value 必须在写路径之前失败（ToolParam 已声明 value 必填）；
/// 2) type 大小写必须归一化，不得通过 OrdinalIgnoreCase 校验后落到通用分支写入空内容；
/// 3) delete 的 'book' 类型分支必须继续可用。
/// 未知 type 仍然放行（既有合同由 MemoryQualityFilter 产出 unknown_type 警告），不在此处断言拒绝。
/// </summary>
[TestClass]
public sealed class SaveMemoryTypeContractTests
{
    [TestMethod]
    public async Task UpsertPreferenceWithoutValue_FailsClosedWithoutWriting()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "upsert",
            ["type"] = "preference",
            ["key"] = "locale",
        });

        Assert.IsFalse(result.Success, "value is declared required in SaveMemoryArgs and must be enforced");
        Assert.AreEqual(0, library.UpsertCalls, "invalid parameter combination must write nothing");
    }

    [TestMethod]
    public async Task UppercaseTypePreference_IsNormalizedAndWritesKeyValueContent()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "upsert",
            ["type"] = "Preference",
            ["key"] = "locale",
            ["value"] = "zh-CN",
        });

        Assert.AreEqual(1, library.UpsertCalls, "type is case-insensitive and must reach the write path");
        Assert.AreEqual("locale: zh-CN", library.LastExperience?.Content,
            "case-insensitive validation must not route to a blank-content generic write");
    }

    [TestMethod]
    public async Task PaddedUppercaseType_IsAlsoNormalized()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "  UPSERT  ",
            ["type"] = "  PREFERENCE  ",
            ["key"] = "locale",
            ["value"] = "zh-CN",
        });

        Assert.AreEqual(1, library.UpsertCalls);
        Assert.AreEqual("locale: zh-CN", library.LastExperience?.Content);
    }

    [TestMethod]
    public async Task DeleteWithBookType_IsStillAccepted()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "delete",
            ["type"] = "Book",
            ["book"] = "some-book",
        });

        Assert.IsFalse(result.Success, "the stub library rejects every operation, but the type gate must let the call through");
        Assert.IsNull(result.Error!.Contains("Unknown memory type", StringComparison.Ordinal) ? result.Error : null,
            "delete must keep accepting the existing 'book' type branch");
    }

    private static SaveMemoryTool CreateTool(RecordingMemoryLibraryConvenience library)
        => new(library, null!, NullLogger<SaveMemoryTool>.Instance);

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

    /// <summary>只记录写入是否发生；命中即失败，用于证明 fail-closed 分支没有落到写路径。</summary>
    private sealed class RecordingMemoryLibraryConvenience : IMemoryLibraryConvenience
    {
        public int UpsertCalls { get; private set; }
        public ExperiencePackage? LastExperience { get; private set; }

        public Task<ExperienceWriteResult> UpsertExperienceAsync(
            string workspaceId, ExperiencePackage experience, CancellationToken ct = default)
        {
            UpsertCalls++;
            LastExperience = experience;
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
}
