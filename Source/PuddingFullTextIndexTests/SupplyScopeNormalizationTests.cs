using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure.Supply;

namespace PuddingFullTextIndexTests;

/// <summary>
/// A1（断言）scope 规范化：空 / 相对 / 不存在 / 非目录 / 规范化后重复 / 嵌套（两方向）**逐条**拒绝，
/// 且每条拒绝都带「值 + 原因枚举 + 可读消息」。
/// </summary>
[TestClass]
public sealed class SupplyScopeNormalizationTests
{
    [TestMethod]
    public void Empty_Scope_Is_Rejected_With_Empty_Reason()
    {
        var result = SupplyScopeNormalizer.Normalize(new[] { "   " });

        Assert.AreEqual(0, result.Accepted.Count);
        AssertRejection(result.Rejections, SupplyRejectionReason.Empty, expectedValue: "   ");
    }

    [TestMethod]
    public void Empty_Request_Is_Rejected_With_Empty_Reason()
    {
        var result = SupplyScopeNormalizer.Normalize(Array.Empty<string>());

        Assert.AreEqual(0, result.Accepted.Count);
        Assert.AreEqual(1, result.Rejections.Count);
        Assert.AreEqual(SupplyRejectionReason.Empty, result.Rejections[0].Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Rejections[0].Message));
    }

    [TestMethod]
    public void Relative_Scope_Is_Rejected_With_Relative_Reason()
    {
        var result = SupplyScopeNormalizer.Normalize(new[] { @"Source\PuddingFullTextIndex" });

        Assert.AreEqual(0, result.Accepted.Count);
        AssertRejection(result.Rejections, SupplyRejectionReason.Relative, @"Source\PuddingFullTextIndex");
    }

    [TestMethod]
    public void Missing_Directory_Is_Rejected_With_NotFound_Reason()
    {
        using var fixture = new TempSupplyFixture();
        var missing = Path.Combine(fixture.Root, "does-not-exist");

        var result = SupplyScopeNormalizer.Normalize(new[] { missing });

        Assert.AreEqual(0, result.Accepted.Count);
        AssertRejection(result.Rejections, SupplyRejectionReason.NotFound, missing);
    }

    [TestMethod]
    public void File_Path_Is_Rejected_With_NotDirectory_Reason()
    {
        using var fixture = new TempSupplyFixture();
        var file = fixture.Write("a.cs", "class A { }");

        var result = SupplyScopeNormalizer.Normalize(new[] { file });

        Assert.AreEqual(0, result.Accepted.Count);
        AssertRejection(result.Rejections, SupplyRejectionReason.NotDirectory, file);
    }

    [TestMethod]
    public void Duplicate_Scope_After_Normalization_Is_Rejected()
    {
        using var fixture = new TempSupplyFixture();

        var result = SupplyScopeNormalizer.Normalize(new[] { fixture.Corpus, fixture.Corpus + @"\" });

        Assert.AreEqual(1, result.Accepted.Count, "第一次出现应被接受");
        Assert.AreEqual(1, result.Rejections.Count);
        Assert.AreEqual(SupplyRejectionReason.Duplicate, result.Rejections[0].Reason);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Rejections[0].Value));
        StringAssert.Contains(result.Rejections[0].Message, "重复");
    }

    [TestMethod]
    public void Nested_Scope_Parent_Then_Child_Is_Rejected()
    {
        using var fixture = new TempSupplyFixture();
        var child = fixture.CreateDirectory("sub");

        var result = SupplyScopeNormalizer.Normalize(new[] { fixture.Corpus, child });

        Assert.AreEqual(1, result.Accepted.Count);
        Assert.AreEqual(1, result.Rejections.Count);
        Assert.AreEqual(SupplyRejectionReason.Nested, result.Rejections[0].Reason);
        Assert.AreEqual(child, result.Rejections[0].Value);
        StringAssert.Contains(result.Rejections[0].Message, "嵌套");
        StringAssert.Contains(result.Rejections[0].Message, fixture.Corpus);
    }

    [TestMethod]
    public void Nested_Scope_Child_Then_Parent_Is_Rejected()
    {
        using var fixture = new TempSupplyFixture();
        var child = fixture.CreateDirectory("sub");

        var result = SupplyScopeNormalizer.Normalize(new[] { child, fixture.Corpus });

        Assert.AreEqual(1, result.Accepted.Count);
        Assert.AreEqual(1, result.Rejections.Count);
        Assert.AreEqual(SupplyRejectionReason.Nested, result.Rejections[0].Reason);
        Assert.AreEqual(fixture.Corpus, result.Rejections[0].Value);
        StringAssert.Contains(result.Rejections[0].Message, "嵌套");
        StringAssert.Contains(result.Rejections[0].Message, child);
    }

    [TestMethod]
    public void Normalization_Resolves_DotDot_And_Trims_Trailing_Separator()
    {
        using var fixture = new TempSupplyFixture();
        var sub = fixture.CreateDirectory("sub");

        var result = SupplyScopeNormalizer.Normalize(new[] { Path.Combine(sub, "..", "sub") + Path.DirectorySeparatorChar });

        Assert.AreEqual(0, result.Rejections.Count);
        Assert.AreEqual(1, result.Accepted.Count);
        Assert.AreEqual(sub, result.Accepted[0].RootPath, ".. 必须被解析、尾分隔符必须被去掉");
        Assert.AreEqual(SupplyScopeNormalizer.ToScopeKey(sub), result.Accepted[0].ScopeKey);
        Assert.AreEqual(result.Accepted[0].ScopeKey, result.Accepted[0].ScopeKey.ToLowerInvariant(), "规范键必须是不变文化小写");
    }

    [TestMethod]
    public void Disjoint_Sibling_Scopes_Are_Both_Accepted()
    {
        using var fixture = new TempSupplyFixture();
        var a = fixture.CreateDirectory("a");
        var b = fixture.CreateDirectory("b");

        var result = SupplyScopeNormalizer.Normalize(new[] { a, b });

        Assert.AreEqual(2, result.Accepted.Count);
        Assert.AreEqual(0, result.Rejections.Count);
    }

    [TestMethod]
    public async Task BuildAsync_Surfaces_Rejection_Details_Without_Creating_A_Job()
    {
        using var fixture = new TempSupplyFixture();
        var builder = new StubSupplyBuilder();
        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(fixture.Options));

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(@"relative\scope"));

        Assert.AreEqual(SupplyOutcome.Rejected, outcome.Outcome);
        Assert.IsNull(outcome.JobId);
        Assert.AreEqual(1, outcome.Scopes.Count);
        Assert.AreEqual(SupplyOutcome.Rejected, outcome.Scopes[0].Outcome);
        Assert.IsTrue(outcome.Scopes[0].Reason!.Contains("绝对路径", StringComparison.Ordinal));
        Assert.AreEqual(0, builder.BuildCallCount);
        Assert.AreEqual(0, (await coordinator.ListStatusAsync()).Count, "被拒绝的请求不得创建任何 job");
    }

    private static void AssertRejection(
        IReadOnlyList<SupplyScopeRejection> rejections,
        SupplyRejectionReason expectedReason,
        string expectedValue)
    {
        Assert.AreEqual(1, rejections.Count, "应恰好产生一条拒绝记录");
        var rejection = rejections[0];

        Assert.AreEqual(expectedReason, rejection.Reason, $"原因枚举不符：{rejection.Message}");
        Assert.AreEqual(expectedValue, rejection.Value, "拒绝记录必须带上被拒的值");
        Assert.IsFalse(string.IsNullOrWhiteSpace(rejection.Message), "拒绝记录必须带可读消息");
    }
}
