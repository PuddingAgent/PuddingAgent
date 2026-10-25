using PuddingPlatform.Services.AgentChat;

namespace PuddingPlatformTests.Services;

/// <summary>
/// 思考强度接入点：入站 metadata 键 → LlmRouteSnapshot.ReasoningEffort 的读取语义。
/// 这一跳原先没有测试，而它改错键名仍能编译通过、只会静默失效
/// （前端发 <c>reasoningEffort</c>，后端读别的键 ⇒ 请求永远不带参数、也不报错）。
/// 因此这里把键名与透传语义一起钉住。
/// </summary>
[TestClass]
public sealed class ExecutionRunCoordinatorReasoningEffortTests
{
    /// <summary>
    /// 1) 前端契约键名 <c>reasoningEffort</c> 必须被读到。
    /// 键名与 web 侧 IntentConsole 发送的 metadata 键一致；任一侧改名这条会红。
    /// </summary>
    [TestMethod]
    [DataRow("high")]
    [DataRow("max")]
    public void ReadReasoningEffort_ReadsContractedKey(string effort)
    {
        var json = "{\"reasoningEffort\":\"" + effort + "\",\"other\":\"x\"}";

        Assert.AreEqual(effort, ExecutionRunCoordinator.ReadReasoningEffort(json));
    }

    /// <summary>
    /// 2) 未表态时必须返回 null，让合成处回落到产品默认——
    /// 不在这里替用户选档位，否则「谁决定的」就不可归因了。
    /// </summary>
    [TestMethod]
    public void ReadReasoningEffort_NoMetadata_ReturnsNull()
    {
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort(null));
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort(string.Empty));
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort("   "));
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort("{\"inputMode\":\"text\"}"));
    }

    /// <summary>
    /// 3) 非法值不在入口丢弃：原值透传给 ReasoningEffortPolicy 裁决，
    /// 这样被拒绝的值才能被记录（RejectedValue），而不是静默变成「用户没选」。
    /// </summary>
    [TestMethod]
    public void ReadReasoningEffort_PassesIllegalValueThrough()
    {
        Assert.AreEqual(
            "bogus",
            ExecutionRunCoordinator.ReadReasoningEffort("{\"reasoningEffort\":\"bogus\"}"));
    }

    /// <summary>
    /// 4) 坏 JSON 不得抛异常——这是消息入口，抛错会让整轮对话失败；
    /// 返回 null 等价于「本轮未表态」。
    /// </summary>
    [TestMethod]
    public void ReadReasoningEffort_MalformedJson_ReturnsNull()
    {
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort("{not json"));
        Assert.IsNull(ExecutionRunCoordinator.ReadReasoningEffort("[]"));
    }

    /// <summary>
    /// 5) 键名大小写敏感（Dictionary 默认 ordinal 比较）。
    /// 这条不是鼓励大小写敏感，而是把现状写成契约：
    /// 若将来改成忽略大小写，必须是有意识的选择而不是顺手改掉。
    /// </summary>
    [TestMethod]
    public void ReadReasoningEffort_KeyIsCaseSensitive()
    {
        Assert.IsNull(
            ExecutionRunCoordinator.ReadReasoningEffort("{\"ReasoningEffort\":\"max\"}"));
    }
}
