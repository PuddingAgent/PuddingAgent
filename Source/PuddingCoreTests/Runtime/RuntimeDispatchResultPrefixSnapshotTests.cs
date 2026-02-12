using PuddingCode.Platform;
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class RuntimeDispatchResultPrefixSnapshotTests
{
    [TestMethod]
    public void RuntimeDispatchResult_CanCarryPrefixSnapshot()
    {
        var snapshot = new PromptPrefixSnapshot
        {
            PrefixHash = "prefix-a",
            SystemPromptHash = "system-a",
            ToolSpecHash = "tool-a",
        };

        var result = new RuntimeDispatchResult
        {
            SessionId = "s1",
            AgentInstanceId = "agent-1",
            IsSuccess = true,
            PrefixSnapshot = snapshot,
        };

        Assert.AreSame(snapshot, result.PrefixSnapshot);
    }
}
