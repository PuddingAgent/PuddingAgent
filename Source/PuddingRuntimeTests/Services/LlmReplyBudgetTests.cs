using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 单轮回复上限口径：资源池的 MaxOutputTokens 是模型最大输出**能力**，不是本轮要申请的量。
/// 这里锁住「请求的 max_tokens」与「窗口预留」同口径，防止任何一侧被单独改回登记值。
/// </summary>
[TestClass]
public sealed class LlmReplyBudgetTests
{
    [TestMethod]
    public void RequestTokens_ClampsRegisteredMaximumDownToThePerTurnCap()
    {
        Assert.AreEqual<int?>(LlmReplyBudget.PerTurnOutputCapTokens, LlmReplyBudget.ResolveRequestTokens(393_216));
        Assert.AreEqual<int?>(LlmReplyBudget.PerTurnOutputCapTokens, LlmReplyBudget.ResolveRequestTokens(int.MaxValue));
    }

    [TestMethod]
    public void RequestTokens_KeepsSmallerOrUnregisteredValues()
    {
        Assert.AreEqual<int?>(8_192, LlmReplyBudget.ResolveRequestTokens(8_192));
        Assert.AreEqual<int?>(0, LlmReplyBudget.ResolveRequestTokens(0), "登记为 0 交给网关默认，不臆造上限");
        Assert.IsNull(LlmReplyBudget.ResolveRequestTokens(null), "未登记时不得凭空造出上限");
    }

    [TestMethod]
    public void ReservedTokens_AlwaysMatchesWhatWeRequest()
    {
        Assert.AreEqual(LlmReplyBudget.ResolveRequestTokens(393_216)!.Value, LlmReplyBudget.ResolveReservedTokens(393_216));
        Assert.AreEqual(8_192, LlmReplyBudget.ResolveReservedTokens(8_192));
        Assert.AreEqual(0, LlmReplyBudget.ResolveReservedTokens(null));
        Assert.AreEqual(0, LlmReplyBudget.ResolveReservedTokens(-5), "脏数据不得变成负预留");
    }
}
