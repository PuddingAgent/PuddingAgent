using PuddingCode.Models;
using PuddingContextPruning;

namespace PuddingContextPruningTests;

/// <summary>
/// S2 行为测试：逐条锁住 <c>DeterministicPayloadCompaction</c> 的语义。
///
/// 每个用例都刻意写成「实现改坏就会红」的形式 —— 例如受保护尾部用例断言**引用相等**
/// 而不是内容相等，因为「内容相等」在实现整体没跑的时候也会成立（空转绿）。
/// </summary>
[TestClass]
public sealed class DeterministicPayloadCompactionTests
{
    /// <summary>受保护尾部关闭、回收地板归零：只验证策略语义时使用。</summary>
    private static DeterministicPruneOptions Lenient(int protectedTailMessages = 0) => new()
    {
        ProtectedTailMessages = protectedTailMessages,
        MinReclaimTokens = 0,
        MinReclaimRatio = 0,
    };

    private static string Payload(char fill, int length) => new(fill, length);

    private static ChatMessage UserMessage(string content) => new(ChatRole.User, content);

    private static ChatMessage ToolMessage(string content, string toolCallId = "tc-1", string toolName = "read_file")
        => new(ChatRole.Tool, content, toolCallId, toolName);

    private static ChatMessage AssistantWithCalls(params ToolCall[] calls)
        => new(ChatRole.Assistant, null, ToolCalls: calls);

    /// <summary>把整份上下文压成一个可比对的指纹，用于确定性/不可变性断言。</summary>
    private static string Fingerprint(IReadOnlyList<ChatMessage> messages)
        => string.Join(
            "\u001f",
            messages.Select(m =>
                $"{m.Role}|{m.Content}|{m.ToolCallId}|{m.ToolName}|{m.ToolCalls?.Count ?? 0}|{m.ToolCalls?.FirstOrDefault()?.ArgumentsJson}"));

    // ── 0. 总开关 ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Disabled_Returns_The_Input_List_Instance_And_Touches_Nothing()
    {
        var oversized = Payload('x', 20_000);
        var input = new List<ChatMessage> { UserMessage("hi"), ToolMessage(oversized) };

        var outcome = DeterministicPayloadCompaction.TryApply(
            input,
            new DeterministicPruneOptions { Enabled = false });

        Assert.AreSame(input, outcome.Messages, "disabled 必须返回入参列表实例（零拷贝、零改写）");
        Assert.AreEqual(PruneNoOpReason.Disabled, outcome.NoOpReason);
        Assert.IsFalse(outcome.Changed);
        Assert.AreEqual(PruneStats.Zero, outcome.Stats);
        Assert.AreSame(input[1], outcome.Messages[1], "元素引用不得变化");
        Assert.AreEqual(oversized, outcome.Messages[1].Content);
    }

    // ── 4.1 受保护尾部 ───────────────────────────────────────────────────────────

    [TestMethod]
    public void Protected_Tail_Messages_Are_Returned_By_Reference()
    {
        var input = new List<ChatMessage> { ToolMessage(Payload('a', 24_000), "tc-old") };
        for (var index = 0; index < 11; index++)
        {
            input.Add(UserMessage($"u{index}"));
        }

        for (var index = 0; index < 8; index++)
        {
            input.Add(ToolMessage(Payload((char)('m' + index), 24_000), $"tc-tail-{index}"));
        }

        Assert.HasCount(20, input);

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient(protectedTailMessages: 8));

        Assert.IsTrue(outcome.Changed, "非尾部那条超长工具结果必须被摘要，否则本用例对尾部判据没有区分力");
        Assert.AreEqual(1, outcome.Stats.SummarizedToolResults);
        Assert.AreNotSame(input[0], outcome.Messages[0], "非尾部超长结果应被替换");

        for (var index = 12; index < input.Count; index++)
        {
            Assert.AreSame(input[index], outcome.Messages[index], $"尾部第 {index} 条必须按引用原样返回");
        }
    }

    [TestMethod]
    public void Tail_Boundary_Backtracks_So_A_Tool_Pair_Is_Never_Split()
    {
        var args = "{\"path\":\"" + Payload('p', 5_000) + "\"}";
        var result = Payload('r', 20_000);

        var input = new List<ChatMessage>();
        for (var index = 0; index < 7; index++)
        {
            input.Add(UserMessage($"u{index}"));
        }

        input.Add(AssistantWithCalls(new ToolCall("call-1", "read_file", args)));
        input.Add(ToolMessage(result, "call-1"));
        input.Add(UserMessage("done"));

        Assert.HasCount(10, input);

        // tailStart = 10 - 2 = 8 落在 tool 结果上 ⇒ 必须回退到它的 assistant 发起方（index 7）。
        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient(protectedTailMessages: 2));

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreSame(input[7], outcome.Messages[7], "配对中的 assistant 必须一起受保护");
        Assert.AreSame(input[8], outcome.Messages[8], "配对中的 tool 结果必须一起受保护");
        Assert.AreEqual(args, outcome.Messages[7].ToolCalls![0].ArgumentsJson);
        Assert.AreEqual(result, outcome.Messages[8].Content);
    }

    // ── 4.3 去重 ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Duplicate_Tool_Results_Keep_The_Newest_Full_Body()
    {
        var shared = Payload('z', 1_000);
        var input = new List<ChatMessage>
        {
            ToolMessage(shared, "tc-1"),
            ToolMessage(shared, "tc-2"),
            ToolMessage(shared, "tc-3"),
        };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.IsTrue(outcome.Changed);
        Assert.AreEqual(2, outcome.Stats.DeduplicatedToolResults);
        Assert.AreEqual(0, outcome.Stats.SummarizedToolResults);

        Assert.AreSame(input[2], outcome.Messages[2], "最新一条必须保留全文（引用不变）");
        Assert.AreEqual(shared, outcome.Messages[2].Content);

        StringAssert.Contains(outcome.Messages[0].Content, "toolCallId=tc-3");
        StringAssert.Contains(outcome.Messages[1].Content, "toolCallId=tc-3");
        StringAssert.Contains(outcome.Messages[0].Content, "1000 字符");
        StringAssert.Contains(outcome.Messages[0].Content, "[deterministic-prune]");

        // 配对字段必须原样保留，否则 tool_call / tool_result 会错配。
        Assert.AreEqual("tc-1", outcome.Messages[0].ToolCallId);
        Assert.AreEqual("read_file", outcome.Messages[0].ToolName);
    }

    [TestMethod]
    public void Duplicate_Below_MinDuplicateChars_Is_Not_Deduplicated()
    {
        var shared = Payload('z', 400);
        var input = new List<ChatMessage> { ToolMessage(shared, "tc-1"), ToolMessage(shared, "tc-2") };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreEqual(0, outcome.Stats.DeduplicatedToolResults);
    }

    // ── 4.4 头尾摘要 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Oversized_Tool_Result_Keeps_Head_And_Tail_And_Reports_Original_Length()
    {
        var content = "BEGIN|" + Payload('x', 20_000) + "|END";
        var input = new List<ChatMessage> { ToolMessage(content, "tc-big") };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.IsTrue(outcome.Changed);
        Assert.AreEqual(1, outcome.Stats.SummarizedToolResults);

        var rewritten = outcome.Messages[0];
        Assert.AreNotSame(input[0], rewritten);
        Assert.IsTrue(rewritten.Content!.StartsWith(content[..1_600], StringComparison.Ordinal), "头 1600 字符必须逐字保留");
        Assert.IsTrue(rewritten.Content!.EndsWith(content[^400..], StringComparison.Ordinal), "尾 400 字符必须逐字保留");
        StringAssert.Contains(rewritten.Content!, $"原文 {content.Length} 字符");
        StringAssert.Contains(rewritten.Content!, "conversation_events");
        StringAssert.Contains(rewritten.Content!, "toolCallId=tc-big");
        Assert.IsLessThan(content.Length / 4, rewritten.Content!.Length, "摘要后长度必须显著变短");

        // 只有正文被改写，配对与角色不变。
        Assert.AreEqual(ChatRole.Tool, rewritten.Role);
        Assert.AreEqual("tc-big", rewritten.ToolCallId);
        Assert.AreEqual("read_file", rewritten.ToolName);
    }

    [TestMethod]
    public void Short_Tool_Result_Is_Left_Untouched()
    {
        var input = new List<ChatMessage> { ToolMessage(Payload('s', 100), "tc-small") };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreSame(input[0], outcome.Messages[0]);
    }

    // ── 4.5 工具参数截断 ─────────────────────────────────────────────────────────

    [TestMethod]
    public void Oversized_Tool_Arguments_Are_Truncated_But_Id_And_Name_Survive()
    {
        var args = "{\"path\":\"" + Payload('a', 5_000) + "\"}";
        var input = new List<ChatMessage>
        {
            AssistantWithCalls(new ToolCall("call-1", "read_file", args)),
            UserMessage("next"),
        };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.IsTrue(outcome.Changed);
        Assert.AreEqual(1, outcome.Stats.TruncatedToolArguments);
        Assert.AreSame(input[1], outcome.Messages[1], "非候选消息必须按引用复用");

        var rewritten = outcome.Messages[0].ToolCalls![0];
        Assert.AreEqual("call-1", rewritten.Id);
        Assert.AreEqual("read_file", rewritten.Name);
        Assert.IsLessThan(args.Length, rewritten.ArgumentsJson!.Length);
        StringAssert.Contains(rewritten.ArgumentsJson!, "工具参数已截断");
        StringAssert.Contains(rewritten.ArgumentsJson!, $"原文 {args.Length} 字符");
        Assert.IsTrue(args.StartsWith(rewritten.ArgumentsJson![..512], StringComparison.Ordinal));
    }

    [TestMethod]
    public void Normal_Sized_Tool_Arguments_Keep_The_Same_Message_Reference()
    {
        var input = new List<ChatMessage>
        {
            AssistantWithCalls(new ToolCall("call-1", "read_file", "{\"path\":\"a.cs\"}")),
            UserMessage("next"),
        };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreSame(input[0], outcome.Messages[0]);
    }

    // ── 4.6 准入地板 ─────────────────────────────────────────────────────────────

    [TestMethod]
    public void Reclaim_Below_MinReclaimTokens_Rolls_Back_To_The_Input_Instance()
    {
        var input = new List<ChatMessage> { ToolMessage(Payload('r', 3_000), "tc-mid") };

        // 默认 MinReclaimTokens = 2000：3000 字符只省下约 200 token，必须整份回退。
        var outcome = DeterministicPayloadCompaction.TryApply(
            input,
            new DeterministicPruneOptions { ProtectedTailMessages = 0, MinToolResultChars = 2_500 });

        Assert.AreEqual(PruneNoOpReason.ReclaimBelowMinimum, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages, "回收不足时必须返回入参列表实例");
        Assert.AreEqual(1, outcome.Stats.SummarizedToolResults, "策略确实命中了");
        Assert.AreEqual(0, outcome.Stats.ReclaimedTokens);
        Assert.AreEqual(outcome.Stats.BeforeTokens, outcome.Stats.AfterTokens);
        Assert.IsGreaterThan(0, outcome.Stats.BeforeTokens);
        Assert.IsFalse(outcome.Changed);
    }

    [TestMethod]
    public void Reclaim_Below_MinReclaimRatio_Rolls_Back_To_The_Input_Instance()
    {
        var input = new List<ChatMessage> { ToolMessage(Payload('r', 3_000), "tc-mid") };

        // token 地板归零，但比例地板 0.9：省下的比例远不够。
        var outcome = DeterministicPayloadCompaction.TryApply(
            input,
            new DeterministicPruneOptions
            {
                ProtectedTailMessages = 0,
                MinToolResultChars = 2_500,
                MinReclaimTokens = 0,
                MinReclaimRatio = 0.9,
            });

        Assert.AreEqual(PruneNoOpReason.ReclaimBelowMinimum, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreEqual(0, outcome.Stats.ReclaimedTokens);
    }

    // ── 4.7 确定性 / 入参不可变 ──────────────────────────────────────────────────

    [TestMethod]
    public void Two_Calls_On_Equal_Inputs_Produce_Byte_Identical_Output()
    {
        static List<ChatMessage> Build()
        {
            var shared = Payload('d', 1_200);
            return
            [
                ToolMessage(shared, "tc-1"),
                ToolMessage(shared, "tc-2"),
                ToolMessage(Payload('e', 20_000), "tc-3"),
                AssistantWithCalls(new ToolCall("call-4", "write_file", Payload('f', 5_000))),
                UserMessage("tail"),
            ];
        }

        var first = DeterministicPayloadCompaction.TryApply(Build(), Lenient());
        var second = DeterministicPayloadCompaction.TryApply(Build(), Lenient());

        Assert.IsTrue(first.Changed);
        Assert.AreEqual(Fingerprint(first.Messages), Fingerprint(second.Messages), "同输入两次调用必须逐字相同");
        Assert.AreEqual(first.Stats, second.Stats);
        Assert.AreEqual(first.NoOpReason, second.NoOpReason);
    }

    [TestMethod]
    public void Input_List_And_Its_Elements_Are_Never_Mutated()
    {
        var shared = Payload('d', 1_200);
        var input = new List<ChatMessage>
        {
            ToolMessage(shared, "tc-1"),
            ToolMessage(shared, "tc-2"),
            AssistantWithCalls(new ToolCall("call-3", "write_file", Payload('f', 5_000))),
        };

        var before = Fingerprint(input);
        var argumentsBefore = input[2].ToolCalls![0].ArgumentsJson;
        var countBefore = input.Count;

        _ = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(before, Fingerprint(input), "入参内容不得变化");
        Assert.AreEqual(argumentsBefore, input[2].ToolCalls![0].ArgumentsJson);
        Assert.HasCount(countBefore, input);
        Assert.AreEqual(shared, input[0].Content);
    }

    // ── 4.8 无操作原因 ───────────────────────────────────────────────────────────

    [TestMethod]
    public void All_Payloads_Inside_The_Protected_Tail_Yields_TailOnly()
    {
        var input = new List<ChatMessage>
        {
            UserMessage("a"),
            ToolMessage(Payload('t', 20_000), "tc-1"),
            UserMessage("b"),
        };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient(protectedTailMessages: 3));

        Assert.AreEqual(PruneNoOpReason.TailOnly, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
    }

    [TestMethod]
    public void Nothing_Eligible_When_All_Non_Tail_Messages_Are_Small()
    {
        var input = new List<ChatMessage>
        {
            UserMessage("hello"),
            AssistantWithCalls(new ToolCall("call-1", "read_file", "{\"path\":\"a.cs\"}")),
            ToolMessage("short result", "call-1"),
        };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
    }

    [TestMethod]
    public void Empty_Input_Yields_NoMessages()
    {
        var input = new List<ChatMessage>();
        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NoMessages, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
    }

    // ── 跳过规则 ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Context_Summary_And_Multimodal_Tool_Results_Are_Never_Rewritten()
    {
        var summary = new ChatMessage(ChatRole.Tool, Payload('s', 20_000), "tc-summary", "read_file")
        {
            IsContextSummary = true,
        };
        var multimodal = new ChatMessage(
            ChatRole.Tool,
            null,
            "tc-parts",
            "read_file",
            ContentParts: [new LlmTextPart(Payload('p', 20_000))]);
        var input = new List<ChatMessage> { summary, multimodal };

        var outcome = DeterministicPayloadCompaction.TryApply(input, Lenient());

        Assert.AreEqual(PruneNoOpReason.NothingEligible, outcome.NoOpReason);
        Assert.AreSame(input, outcome.Messages);
        Assert.AreSame(summary, outcome.Messages[0]);
        Assert.AreSame(multimodal, outcome.Messages[1]);
    }
}
