using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class CompositionSnapshotTests
{
    private static ChatMessage Sys(string content) => new(ChatRole.System, content);
    private static ChatMessage User(string content) => new(ChatRole.User, content);

    private static ToolParameter Param(string name, string type = "string", string description = "")
        => new(name, type, description);

    private static LlmToolDefinition Tool(
        string name,
        string description = "desc",
        IReadOnlyList<ToolParameter>? properties = null,
        IReadOnlyList<string>? required = null)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = new ToolParameterSchema(
                properties ?? Array.Empty<ToolParameter>(),
                required ?? Array.Empty<string>()),
        };

    // ── System prompt hash ──────────────────────────────

    [TestMethod]
    public void ComputeSystemPromptHash_SameInput_IsStable()
    {
        var a = new[] { Sys("hello"), User("hi") };
        var b = new[] { Sys("hello"), User("hi") };

        Assert.AreEqual(CompositionSnapshot.ComputeSystemPromptHash(a), CompositionSnapshot.ComputeSystemPromptHash(b));
    }

    [TestMethod]
    public void ComputeSystemPromptHash_DifferentContent_Changes()
    {
        Assert.AreNotEqual(
            CompositionSnapshot.ComputeSystemPromptHash(new[] { Sys("hello") }),
            CompositionSnapshot.ComputeSystemPromptHash(new[] { Sys("world") }));
    }

    [TestMethod]
    public void ComputeSystemPromptHash_OnlySystemMessagesAreHashed()
    {
        var withUserNoise = new[] { Sys("core"), User("noise1"), User("noise2") };
        var withoutNoise = new[] { Sys("core") };

        Assert.AreEqual(
            CompositionSnapshot.ComputeSystemPromptHash(withUserNoise),
            CompositionSnapshot.ComputeSystemPromptHash(withoutNoise));
    }

    [TestMethod]
    public void ComputeSystemPromptHash_ConcatenatesMultipleSystemMessagesInOrder()
    {
        var ordered = new[] { Sys("a"), Sys("b") };
        var reversed = new[] { Sys("b"), Sys("a") };

        Assert.AreEqual(
            CompositionSnapshot.ComputeSystemPromptHash(ordered),
            CompositionSnapshot.ComputeSystemPromptHash(new[] { Sys("a\nb") }));
        Assert.AreNotEqual(
            CompositionSnapshot.ComputeSystemPromptHash(ordered),
            CompositionSnapshot.ComputeSystemPromptHash(reversed));
    }

    [TestMethod]
    public void ComputeSystemPromptHash_EmptyMessages_IsEmptyHash()
    {
        Assert.AreEqual(
            CompositionSnapshot.ComputeSystemPromptHash(Array.Empty<ChatMessage>()),
            CompositionSnapshot.Sha256Hex(string.Empty));
    }

    [TestMethod]
    public void ComputeSystemPromptHash_NullContent_TreatedAsEmpty()
    {
        Assert.AreEqual(
            CompositionSnapshot.ComputeSystemPromptHash(new[] { new ChatMessage(ChatRole.System, null) }),
            CompositionSnapshot.Sha256Hex(string.Empty));
    }

    // ── Tool spec hash ─────────────────────────────────

    [TestMethod]
    public void ComputeToolSpecHash_NullTools_HashesEmpty()
    {
        Assert.AreEqual(CompositionSnapshot.ComputeToolSpecHash(null), CompositionSnapshot.Sha256Hex(string.Empty));
    }

    [TestMethod]
    public void ComputeToolSpecHash_EmptyTools_HashesEmpty()
    {
        Assert.AreEqual(
            CompositionSnapshot.ComputeToolSpecHash(Array.Empty<LlmToolDefinition>()),
            CompositionSnapshot.Sha256Hex(string.Empty));
    }

    [TestMethod]
    public void ComputeToolSpecHash_SameInput_IsStable()
    {
        var tools = new[] { Tool("t", "desc", new[] { Param("x") }, new[] { "x" }) };
        Assert.AreEqual(CompositionSnapshot.ComputeToolSpecHash(tools), CompositionSnapshot.ComputeToolSpecHash(tools));
    }

    [TestMethod]
    public void ComputeToolSpecHash_ToolOrderChange_ChangesHash()
    {
        var a = new[] { Tool("a"), Tool("b") };
        var b = new[] { Tool("b"), Tool("a") };
        Assert.AreNotEqual(CompositionSnapshot.ComputeToolSpecHash(a), CompositionSnapshot.ComputeToolSpecHash(b));
    }

    [TestMethod]
    public void ComputeToolSpecHash_DescriptionChange_ChangesHash()
    {
        Assert.AreNotEqual(
            CompositionSnapshot.ComputeToolSpecHash(new[] { Tool("t", "old") }),
            CompositionSnapshot.ComputeToolSpecHash(new[] { Tool("t", "new") }));
    }

    [TestMethod]
    public void ComputeToolSpecHash_ParameterPropertyChange_ChangesHash()
    {
        var a = new[] { Tool("t", properties: new[] { Param("x", "string", "p") }) };
        var b = new[] { Tool("t", properties: new[] { Param("x", "number", "p") }) };
        Assert.AreNotEqual(CompositionSnapshot.ComputeToolSpecHash(a), CompositionSnapshot.ComputeToolSpecHash(b));
    }

    [TestMethod]
    public void ComputeToolSpecHash_StableAcrossPropertyOrder()
    {
        // 同一工具，Properties 构造顺序不同但集合相同 → hash 必须一致（字节级稳定）。
        var a = new[] { Tool("t", properties: new[] { Param("b", "string"), Param("a", "number"), Param("c", "integer") }) };
        var b = new[] { Tool("t", properties: new[] { Param("c", "integer"), Param("b", "string"), Param("a", "number") }) };

        Assert.AreEqual(CompositionSnapshot.ComputeToolSpecHash(a), CompositionSnapshot.ComputeToolSpecHash(b));
    }

    [TestMethod]
    public void ComputeToolSpecHash_StableAcrossRepeatedCalls()
    {
        var tools = new[]
        {
            Tool("t", "desc", new[] { Param("c", "integer"), Param("a", "string"), Param("b", "number") }, new[] { "a", "b", "c" }),
        };

        var first = CompositionSnapshot.ComputeToolSpecHash(tools);
        for (var i = 0; i < 3; i++)
            Assert.AreEqual(first, CompositionSnapshot.ComputeToolSpecHash(tools));
    }

    [TestMethod]
    public void ComputeToolSpecHash_DiffersWhenPropertySetDiffers()
    {
        var a = new[] { Tool("t", properties: new[] { Param("x", "string") }) };
        var b = new[] { Tool("t", properties: new[] { Param("x", "string"), Param("y", "string") }) };
        Assert.AreNotEqual(CompositionSnapshot.ComputeToolSpecHash(a), CompositionSnapshot.ComputeToolSpecHash(b));
    }

    // ── Prefix hash ────────────────────────────────────

    [TestMethod]
    public void ComputePrefixHash_IsDeterministic_AndSensitiveToInputs()
    {
        Assert.AreEqual(CompositionSnapshot.ComputePrefixHash("a", "b"), CompositionSnapshot.ComputePrefixHash("a", "b"));
        Assert.AreNotEqual(CompositionSnapshot.ComputePrefixHash("a", "b"), CompositionSnapshot.ComputePrefixHash("a", "c"));
        Assert.AreNotEqual(CompositionSnapshot.ComputePrefixHash("a", "b"), CompositionSnapshot.ComputePrefixHash("b", "a"));
    }

    [TestMethod]
    public void Sha256Hex_IsLowercaseHex64()
    {
        var hash = CompositionSnapshot.Sha256Hex("abc");
        Assert.AreEqual(64, hash.Length);
        Assert.AreEqual(hash, hash.ToLowerInvariant());
    }

    // ── Version registry ───────────────────────────────

    [TestMethod]
    public void Registry_VersionIncrementsAndReuses()
    {
        var reg = new CompositionVersionRegistry();

        var first = reg.Observe("s", "sys1", "tool1");
        var same = reg.Observe("s", "sys1", "tool1");
        var changed = reg.Observe("s", "sys2", "tool1");
        var back = reg.Observe("s", "sys1", "tool1");

        Assert.AreEqual(1, first.Version);
        Assert.AreEqual("initial", first.ChangeReason);

        Assert.AreEqual(1, same.Version);
        Assert.AreEqual("none", same.ChangeReason);

        Assert.AreEqual(2, changed.Version);
        Assert.AreEqual("system_prompt_changed", changed.ChangeReason);

        Assert.AreEqual(1, back.Version);          // 复用既有组合
        Assert.AreEqual("system_prompt_changed", back.ChangeReason); // 相对上次(sys2)变化
    }

    [TestMethod]
    public void Registry_ToolSpecChange_IsDetected()
    {
        var reg = new CompositionVersionRegistry();
        reg.Observe("s", "sys", "toolA");
        var obs = reg.Observe("s", "sys", "toolB");

        Assert.AreEqual("tool_spec_changed", obs.ChangeReason);
        Assert.AreEqual(2, obs.Version);
    }

    [TestMethod]
    public void Registry_BothChanged_IsDetected()
    {
        var reg = new CompositionVersionRegistry();
        reg.Observe("s", "sysA", "toolA");
        var obs = reg.Observe("s", "sysB", "toolB");

        Assert.AreEqual("system_prompt_changed,tool_spec_changed", obs.ChangeReason);
        Assert.AreEqual(2, obs.Version);
    }

    [TestMethod]
    public void Registry_SessionsAreIndependent()
    {
        var reg = new CompositionVersionRegistry();
        var s1 = reg.Observe("s1", "sys", "tool");
        var s2 = reg.Observe("s2", "sys", "tool");

        Assert.AreEqual(1, s1.Version);
        Assert.AreEqual(1, s2.Version);
        Assert.AreEqual("initial", s1.ChangeReason);
        Assert.AreEqual("initial", s2.ChangeReason);
    }

    [TestMethod]
    public void Registry_BlankSessionId_Throws()
    {
        var reg = new CompositionVersionRegistry();
        Assert.ThrowsExactly<ArgumentException>(() => reg.Observe(" ", "sys", "tool"));
    }
}
