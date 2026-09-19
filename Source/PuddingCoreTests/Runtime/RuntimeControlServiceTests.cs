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
    public void RecordError_AggregateFuse_RemainsReachableAboveLegacyQueueCap()
    {
        var service = new RuntimeControlService(maxErrorsInWindow: 12, warningThreshold: 10);
        const string sessionId = "session_aggregate";

        RuntimeFuseResult result = null!;
        for (var i = 0; i < 12; i++)
        {
            result = service.RecordError(
                sessionId,
                RuntimeErrorKind.Tool,
                "tool",
                $"distinct-error-{(char)('a' + i)}");

            if (i < 11)
                Assert.IsFalse(result.Triggered);
        }

        Assert.IsTrue(result.Triggered);
        Assert.AreEqual(12, result.WindowErrorCount);
        Assert.AreEqual(1, result.SameFingerprintCount);
        StringAssert.Contains(result.Summary, "aggregate_error_window");
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

    [TestMethod]
    public void SetMode_Yolo_Persists_ModeStateFile_With_Yolo_Value()
    {
        var path = NewTempModeStatePath();
        try
        {
            var service = new RuntimeControlService(modeStateFilePath: path);

            var result = service.SetMode(RuntimeExecutionMode.Yolo, "test");

            Assert.IsTrue(result.Success);
            Assert.IsTrue(File.Exists(path));
            var json = File.ReadAllText(path);
            StringAssert.Contains(json, "\"mode\":\"Yolo\"");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void Constructor_Restores_Mode_From_State_File_Across_Restart()
    {
        var path = NewTempModeStatePath();
        try
        {
            var first = new RuntimeControlService(modeStateFilePath: path);
            first.SetMode(RuntimeExecutionMode.Yolo, "test");

            var restarted = new RuntimeControlService(modeStateFilePath: path);

            Assert.AreEqual(RuntimeExecutionMode.Yolo, restarted.Mode);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void Constructor_Corrupt_State_File_Falls_Back_To_Normal_Without_Throwing()
    {
        var path = NewTempModeStatePath();
        try
        {
            File.WriteAllText(path, "{ not json");

            var service = new RuntimeControlService(modeStateFilePath: path);

            Assert.AreEqual(RuntimeExecutionMode.Normal, service.Mode);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [TestMethod]
    public void SetMode_Safe_Keeps_Last_Persisted_Steady_State()
    {
        var path = NewTempModeStatePath();
        try
        {
            var service = new RuntimeControlService(modeStateFilePath: path);
            service.SetMode(RuntimeExecutionMode.Yolo, "steady");
            var persisted = File.ReadAllText(path);

            service.SetMode(RuntimeExecutionMode.Safe, "temporary");

            Assert.AreEqual(RuntimeExecutionMode.Safe, service.Mode);
            Assert.AreEqual(persisted, File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string NewTempModeStatePath()
        => Path.Combine(Path.GetTempPath(), $"pudding-runtime-mode-{Guid.NewGuid():N}.json");
}
