using PuddingRuntime.Services;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class LlmInvocationPurposeAccessorTests
{
    [TestMethod]
    public void Push_NestedScopes_RestorePreviousPurpose()
    {
        var accessor = new LlmInvocationPurposeAccessor();

        Assert.AreEqual("agent", accessor.Current);
        using (accessor.Push("approval"))
        {
            Assert.AreEqual("approval", accessor.Current);
            using (accessor.Push("compaction"))
                Assert.AreEqual("compaction", accessor.Current);
            Assert.AreEqual("approval", accessor.Current);
        }
        Assert.AreEqual("agent", accessor.Current);
    }

    [TestMethod]
    public void Push_NormalizesUntrustedPurpose_WithoutChangingPromptData()
    {
        var accessor = new LlmInvocationPurposeAccessor();

        using (accessor.Push(" Approval:Control/Plane "))
            Assert.AreEqual("approvalcontrolp", accessor.Current);
    }
}
