using PuddingCode.Abstractions;
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class RuntimeControlServiceTests
{
    [TestMethod]
    public void RecordError_Triggers_Fuse_For_Consecutive_Similar_Errors()
    {
        var service = new RuntimeControlService();
        const string sessionId = "session_1";

        RuntimeFuseResult result = null!;
        for (var i = 0; i < 5; i++)
        {
            result = service.RecordError(
                sessionId,
                RuntimeErrorKind.Api,
                "llm",
                "Invalid tools[6].function.name does not match pattern");
        }

        Assert.IsTrue(result.Triggered);
        var status = service.GetStatus(sessionId).Session;
        Assert.IsNotNull(status);
        Assert.AreEqual(SessionState.Faulted, status.State);
        StringAssert.Contains(result.Summary, "Session fuse triggered");
        Assert.IsFalse(service.CanInvokeTool(sessionId, "shell").Allowed);
    }

    [TestMethod]
    public void MarkProgress_Resets_Consecutive_Error_Counters()
    {
        var service = new RuntimeControlService();
        const string sessionId = "session_2";

        for (var i = 0; i < 4; i++)
        {
            service.RecordError(
                sessionId,
                RuntimeErrorKind.Tool,
                "shell",
                "same error");
        }

        service.MarkProgress(sessionId);
        var result = service.RecordError(
            sessionId,
            RuntimeErrorKind.Tool,
            "shell",
            "same error");

        Assert.IsFalse(result.Triggered);
        var status = service.GetStatus(sessionId).Session;
        Assert.IsNotNull(status);
        Assert.AreEqual(1, status.WindowErrorCount);
        Assert.AreEqual(1, status.SameFingerprintCount);
    }

    [TestMethod]
    public void ResetSessionFault_Clears_RecentErrors_For_Completed_Session()
    {
        var service = new RuntimeControlService(maxErrorsInWindow: 5, warningThreshold: 3);
        const string sessionId = "session_completed";

        service.RecordError(sessionId, RuntimeErrorKind.Api, "llm", "provider error 1");
        service.RecordError(sessionId, RuntimeErrorKind.Api, "llm", "provider error 2");
        service.MarkSessionCompleted(sessionId);

        var before = service.GetStatus(sessionId).Session;
        Assert.IsNotNull(before);
        Assert.AreEqual(SessionState.Completed, before.State);
        Assert.AreEqual(2, before.RecentErrorCount);

        var result = service.ResetSessionFault(sessionId);

        Assert.IsTrue(result.Success);
        var after = service.GetStatus(sessionId).Session;
        Assert.IsNotNull(after);
        Assert.AreEqual(SessionState.Completed, after.State);
        Assert.AreEqual(0, after.RecentErrorCount);
        Assert.AreEqual(0, after.WindowErrorCount);
        Assert.IsNull(after.FaultSummary);
    }

    [TestMethod]
    public void SafeMode_Blocks_User_Messages_And_Tool_Calls()
    {
        var service = new RuntimeControlService();

        service.SetMode(RuntimeExecutionMode.Safe, "test");

        Assert.IsFalse(service.CanAcceptUserMessage("session_3").Allowed);
        Assert.IsFalse(service.CanInvokeTool("session_3", "shell").Allowed);
    }
}
