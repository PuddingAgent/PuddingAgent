using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class PrefixCacheSnapshotBuilderTests
{
    [TestMethod]
    public void Build_UserMessageAppend_DoesNotChangePrefixHash()
    {
        var tools = new[] { CreateTool("file_read") };
        var first = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding."),
            new ChatMessage(ChatRole.User, "hello"),
        }, tools);

        var second = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding."),
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "hi"),
            new ChatMessage(ChatRole.User, "continue"),
        }, tools);

        Assert.AreEqual(first.PrefixHash, second.PrefixHash);
        Assert.AreEqual(first.SystemPromptHash, second.SystemPromptHash);
        Assert.AreEqual(first.ToolSpecHash, second.ToolSpecHash);
    }

    [TestMethod]
    public void Build_ToolOrderChange_ChangesToolSpecHashAndPrefixHash()
    {
        var messages = new[] { new ChatMessage(ChatRole.System, "You are Pudding.") };

        var first = PrefixCacheSnapshotBuilder.Build(messages, new[]
        {
            CreateTool("file_read"),
            CreateTool("file_write"),
        });

        var second = PrefixCacheSnapshotBuilder.Build(messages, new[]
        {
            CreateTool("file_write"),
            CreateTool("file_read"),
        });

        Assert.AreNotEqual(first.ToolSpecHash, second.ToolSpecHash);
        Assert.AreNotEqual(first.PrefixHash, second.PrefixHash);
        Assert.AreEqual(first.SystemPromptHash, second.SystemPromptHash);
    }

    [TestMethod]
    public void Build_SystemPromptChange_ChangesSystemPromptHashAndPrefixHash()
    {
        var tools = new[] { CreateTool("file_read") };

        var first = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding."),
        }, tools);

        var second = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding with memory."),
        }, tools);

        Assert.AreNotEqual(first.SystemPromptHash, second.SystemPromptHash);
        Assert.AreNotEqual(first.PrefixHash, second.PrefixHash);
        Assert.AreEqual(first.ToolSpecHash, second.ToolSpecHash);
    }

    [TestMethod]
    public void Build_HistoryHeadReplacement_ChangesPrefixHash_WhenSystemAndToolsStaySame()
    {
        var tools = new[] { CreateTool("file_read") };
        var original = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding."),
            new ChatMessage(ChatRole.User, "original task"),
            new ChatMessage(ChatRole.Assistant, "working"),
        }, tools);
        var checkpoint = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, "You are Pudding."),
            new ChatMessage(ChatRole.User, "<compacted-summary>checkpoint</compacted-summary>"),
            new ChatMessage(ChatRole.Assistant, "working"),
        }, tools);

        Assert.AreEqual(original.SystemPromptHash, checkpoint.SystemPromptHash);
        Assert.AreEqual(original.ToolSpecHash, checkpoint.ToolSpecHash);
        Assert.AreNotEqual(original.HistoryAnchorHash, checkpoint.HistoryAnchorHash);
        Assert.AreNotEqual(original.PrefixHash, checkpoint.PrefixHash);
    }

    [TestMethod]
    public void Build_ContextLayerDynamicSuffix_DoesNotPolluteStableSystemHash()
    {
        var first = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, """
                --- CONTEXT-LAYER: L0-STATIC ---
                stable
                --- CONTEXT-LAYER: L6-CONTEXT-AUGMENT ---
                recalled-a
                """),
        }, []);
        var second = PrefixCacheSnapshotBuilder.Build(new[]
        {
            new ChatMessage(ChatRole.System, """
                --- CONTEXT-LAYER: L0-STATIC ---
                stable
                --- CONTEXT-LAYER: L6-CONTEXT-AUGMENT ---
                recalled-b
                """),
        }, []);

        Assert.AreEqual(first.SystemPromptHash, second.SystemPromptHash);
        Assert.AreEqual(first.PrefixHash, second.PrefixHash);
    }

    private static LlmToolDefinition CreateTool(string name) => new()
    {
        Name = name,
        Description = $"Tool {name}",
        Parameters = new ToolParameterSchema(
            new[] { new ToolParameter("path", "string", "File path") },
            new[] { "path" }),
    };
}
