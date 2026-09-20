using PuddingCode.Platform;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// <see cref="ApprovalCode"/> 的单元测试。
/// </summary>
/// <remarks>
/// 为什么这些用例必须存在：确认码是 PuddingController 审批流程的**唯一凭据**
/// （<c>GET /api/approval/*</c> 只返回脱敏投影，确认码仅由持码人离线获得）。
/// 在此之前「确认码的生成与校验」只存在于 <c>InMemoryApprovalService</c> 内部
/// （硬依赖 Redis），**没有任何测试覆盖**——既没有断言它的字符集与熵，
/// 也没有断言校验语义。本文件把这些属性固化为回归契约。
///
/// 明确**不**覆盖的范围：失败尝试限制。当前 <c>ConfirmAsync</c> 尚无该机制，
/// 且其 Redis 交互在本测试环境不可构造（未注册 <c>IConnectionMultiplexer</c>）。
/// </remarks>
[TestClass]
public sealed class ApprovalCodeTests
{
    /// <summary>刻意硬编码：确认码位数是对外契约（用户手输位数），不随常量漂移。</summary>
    private const int ExpectedLength = 8;

    [TestMethod]
    public void Generate_Produces_Eight_Lowercase_Hex_Characters()
    {
        Assert.AreEqual(
            ExpectedLength,
            ApprovalCode.Length,
            "确认码长度是对外契约（用户手输位数）。");

        for (var i = 0; i < 200; i++)
        {
            var code = ApprovalCode.Generate();
            Assert.AreEqual(ExpectedLength, code.Length, $"第 {i} 次生成的确认码长度不符。");
            Assert.IsTrue(
                code.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
                $"第 {i} 次生成的确认码含非小写十六进制字符：{code}");
        }
    }

    [TestMethod]
    public void Generate_Does_Not_Repeat_Across_Many_Draws()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
        {
            var code = ApprovalCode.Generate();
            Assert.IsTrue(
                seen.Add(code),
                $"确认码在第 {i} 次生成时出现重复：{code}（疑似退化为低熵或确定性来源）。");
        }
    }

    [TestMethod]
    public void Generate_Uses_The_Whole_Hex_Alphabet()
    {
        // 2000 次 × 8 字符 = 16000 个 nibble，期望每个取值约 1000 次。
        // 断言 16 个取值全部出现：任何「只从有限集合取值」的降熵实现都会被击中。
        var observed = new HashSet<char>();
        for (var i = 0; i < 2000; i++)
        {
            foreach (var c in ApprovalCode.Generate())
                observed.Add(c);
        }

        Assert.AreEqual(
            16,
            observed.Count,
            $"十六进制字母表未被完整使用，实际出现：{string.Concat(observed.OrderBy(c => c))}");
    }

    [TestMethod]
    public void Matches_Accepts_Exact_Code_And_Rejects_Everything_Else()
    {
        var stored = ApprovalCode.Generate();

        Assert.IsTrue(ApprovalCode.Matches(stored, stored), "完全相同的确认码必须通过。");

        Assert.IsFalse(ApprovalCode.Matches(stored, null), "null 提交不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(stored, ""), "空串不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(null, stored), "存储值为 null 时不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(null, null), "两侧均为 null 不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(stored, stored[..^1]), "提交值少一位不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(stored[..^1], stored), "存储值少一位不得通过。");
        Assert.IsFalse(ApprovalCode.Matches(stored, stored + "0"), "提交值多一位不得通过。");
        Assert.IsFalse(
            ApprovalCode.Matches(stored, new string('f', ExpectedLength)),
            "内容不同不得通过。");
    }

    [TestMethod]
    public void Matches_Is_Case_Sensitive()
    {
        // 用固定字面量而非 Generate()：随机码可能全为数字，转大写后与原值相同，
        // 会让断言变成间歇性失败。
        Assert.IsTrue(ApprovalCode.Matches("abcdef01", "abcdef01"));
        Assert.IsFalse(
            ApprovalCode.Matches("abcdef01", "ABCDEF01"),
            "比较必须区分大小写（生成端恒为小写）。");
    }

    [TestMethod]
    public void Matches_Agrees_With_Ordinal_Equality_On_Result()
    {
        // 恒时比较不改变可观察结果。这里固定住「结果语义与序数相等一致」，
        // 防止将来为实现恒时而误改判定逻辑（例如把不等也放过）。
        var samples = new[] { "00000000", "abcdef01", "12345678", "ffffffff" };
        foreach (var stored in samples)
        {
            foreach (var provided in samples)
            {
                Assert.AreEqual(
                    string.Equals(stored, provided, StringComparison.Ordinal),
                    ApprovalCode.Matches(stored, provided),
                    $"Matches('{stored}','{provided}') 的结果必须与序数相等一致。");
            }
        }
    }
}
