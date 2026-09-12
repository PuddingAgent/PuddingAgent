using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// T01 记忆工具闭合合同的最小回归：
/// 1) 未知 action 必须 fail-closed 且零写入（不得猜成 upsert 落库）；
/// 2) 业务错误必须 Success=false，不能以成功结果返回；
/// 3) 取消必须向上传播，不得被降级成业务错误结果。
/// </summary>
[TestClass]
public sealed class SaveMemoryToolContractTests
{
    [TestMethod]
    public async Task UnknownAction_WithContent_FailsClosedWithoutWriting()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "upsertt",
            ["type"] = "fact",
            ["content"] = "must-not-be-written",
        });

        Assert.IsFalse(result.Success, "unknown action must fail closed");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error!, "upsertt");
        Assert.AreEqual(0, library.UpsertCalls, "unknown action must not write anything");
    }

    [TestMethod]
    public async Task UppercaseAction_IsNormalizedAndReachesLibrary()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "UPSERT",
            ["type"] = "fact",
            ["content"] = "hello",
        });

        Assert.AreEqual(1, library.UpsertCalls, "UPSERT must be normalized to upsert, not rejected as unknown");
        Assert.IsFalse(result.Success, "the stub library intentionally fails the write");
    }

    [TestMethod]
    public async Task BusinessError_IsReportedAsFailureNotSuccess()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        // ImportantMemoryService 未注入 → 业务错误必须是 Success=false。
        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "set_important",
            ["content"] = "x",
        });

        Assert.IsFalse(result.Success, "business error must not be reported as success");
        Assert.IsNotNull(result.Error);
        Assert.AreEqual(0, library.UpsertCalls);
    }

    [TestMethod]
    public async Task Cancellation_PropagatesOutOfTool()
    {
        var library = new RecordingMemoryLibraryConvenience { ThrowCancellation = true };
        var tool = CreateTool(library);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["action"] = "upsert",
                ["type"] = "fact",
                ["content"] = "cancelled",
            },
            cts.Token));
    }

    [TestMethod]
    public async Task GrepMemory_UnknownAction_FailsInsteadOfReportingSuccess()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = new GrepMemoryTool(library, null!, NullLogger<GrepMemoryTool>.Instance);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "searchh",
            ["query"] = "x",
        });

        Assert.IsFalse(result.Success, "unknown grep_memory action must fail closed");
        Assert.IsNotNull(result.Error);
        StringAssert.Contains(result.Error!, "searchh");
    }

    [TestMethod]
    public async Task UpsertPreferenceWithoutKey_FailsClosedWithoutWriting()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "upsert",
            ["type"] = "preference",
            ["key"] = "",
            ["value"] = "orphan value",
        });

        Assert.IsFalse(result.Success, "preference without key must fail before the write path");
        Assert.AreEqual(0, library.UpsertCalls, "invalid parameter combination must write nothing");
    }

    [TestMethod]
    public async Task UpsertFactWithoutContent_FailsClosedWithoutWriting()
    {
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "upsert",
            ["type"] = "fact",
            ["content"] = "",
        });

        Assert.IsFalse(result.Success, "fact without content must fail before the write path");
        Assert.AreEqual(0, library.UpsertCalls);
    }

    [TestMethod]
    public async Task SetImportant_UsesContextDerivedIdentity()
    {
        var important = new RecordingImportantMemoryService();
        var library = new RecordingMemoryLibraryConvenience();
        var tool = CreateTool(library, important);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "set_important",
            ["content"] = "user prefers concise answers",
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("agent", important.LastWriteInstanceId, "identity must derive from the execution context");
        Assert.AreEqual("user prefers concise answers", important.LastWriteContent);
        Assert.AreEqual(0, library.UpsertCalls);
    }

    [TestMethod]
    public async Task GetImportant_UsesContextDerivedIdentity()
    {
        var important = new RecordingImportantMemoryService { Content = "persisted" };
        var tool = CreateTool(new RecordingMemoryLibraryConvenience(), important);

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["action"] = "get_important",
        });

        Assert.IsTrue(result.Success);
        Assert.AreEqual("agent", important.LastReadInstanceId);
        Assert.IsTrue(result.Output.Contains("persisted", StringComparison.Ordinal));
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

        public bool ThrowCancellation { get; init; }

        public Task<ExperienceWriteResult> UpsertExperienceAsync(
            string workspaceId, ExperiencePackage experience, CancellationToken ct = default)
        {
            UpsertCalls++;
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
