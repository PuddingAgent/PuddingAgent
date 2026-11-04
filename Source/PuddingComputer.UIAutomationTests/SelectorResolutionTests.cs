using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingComputer.UIAutomation;

namespace PuddingComputer.UIAutomationTests;

/// <summary>
/// 解析结论判据。这些用例钉住的是「什么情况下不许动手」：
/// 遍历被截断时，既不能断言唯一命中，也不能断言不存在。
/// </summary>
[TestClass]
public sealed class SelectorResolutionTests
{
    [TestMethod]
    public void Decide_SingleHitOnCompleteTraversal_IsOk()
    {
        Assert.AreEqual(ResolutionOutcome.Ok, SelectorResolution.Decide(hitCount: 1, traversalTruncated: false));
    }

    [TestMethod]
    public void Decide_MultipleHits_IsAmbiguousRegardlessOfTruncation()
    {
        Assert.AreEqual(ResolutionOutcome.Ambiguous, SelectorResolution.Decide(hitCount: 2, traversalTruncated: false));
        Assert.AreEqual(ResolutionOutcome.Ambiguous, SelectorResolution.Decide(hitCount: 5, traversalTruncated: true));
    }

    [TestMethod]
    public void Decide_NoHitOnCompleteTraversal_IsNotFound()
    {
        Assert.AreEqual(ResolutionOutcome.NotFound, SelectorResolution.Decide(hitCount: 0, traversalTruncated: false));
    }

    [TestMethod]
    public void Decide_NoHitOnTruncatedTraversal_IsPartialNotNotFound()
    {
        // 「没看完」不能被报成「没有」：否则调用方会去执行一条本不该执行的降级路径。
        Assert.AreEqual(ResolutionOutcome.Partial, SelectorResolution.Decide(hitCount: 0, traversalTruncated: true));
    }

    [TestMethod]
    public void Decide_SingleHitOnTruncatedTraversal_IsPartialNotOk()
    {
        // 截断后只看到一个命中，可能只是其余的还没遍历到。
        Assert.AreEqual(ResolutionOutcome.Partial, SelectorResolution.Decide(hitCount: 1, traversalTruncated: true));
    }
}
