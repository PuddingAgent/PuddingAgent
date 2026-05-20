using PuddingCode.Models;
using PuddingCode.Runtime;

namespace PuddingCoreTests.Runtime;

[TestClass]
public sealed class RuntimeContractTests
{
    [TestMethod]
    public void ExecutionLifecycleRecord_Default_Metadata_Is_Not_Null()
    {
        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_1",
            TraceId = "trace_1",
            WorkspaceId = "default",
            SessionId = "session_1",
            AgentInstanceId = "agent_1",
            Component = "agent_execution",
            Operation = "execute",
            Status = "started",
            StartedAtUtc = DateTimeOffset.UtcNow,
        };

        Assert.IsNotNull(record.Metadata);
        Assert.AreEqual(0, record.Metadata.Count);
    }

    [TestMethod]
    public void ExecutionLifecycleRecord_Required_Fields_Are_Settable()
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ExecutionLifecycleRecord
        {
            ExecutionId = "exec_2",
            TraceId = "trace_2",
            CorrelationId = "corr_2",
            WorkspaceId = "ws",
            SessionId = "sess",
            AgentInstanceId = "agent",
            Component = "llm_gateway",
            Operation = "chat",
            Status = "succeeded",
            StartedAtUtc = now,
            CompletedAtUtc = now.AddMilliseconds(100),
            DurationMs = 100,
            Summary = "ok",
            Error = null,
            Metadata = new Dictionary<string, string> { ["key"] = "val" },
        };

        Assert.AreEqual("exec_2", record.ExecutionId);
        Assert.AreEqual("trace_2", record.TraceId);
        Assert.AreEqual("corr_2", record.CorrelationId);
        Assert.AreEqual("ws", record.WorkspaceId);
        Assert.AreEqual("sess", record.SessionId);
        Assert.AreEqual("agent", record.AgentInstanceId);
        Assert.AreEqual("llm_gateway", record.Component);
        Assert.AreEqual("chat", record.Operation);
        Assert.AreEqual("succeeded", record.Status);
        Assert.AreEqual(100, record.DurationMs);
        Assert.AreEqual("ok", record.Summary);
        Assert.IsNull(record.Error);
        Assert.AreEqual(1, record.Metadata.Count);
        Assert.AreEqual("val", record.Metadata["key"]);
    }

    [TestMethod]
    public void LlmInvocationRequest_Default_Tools_Is_Not_Null()
    {
        var request = new LlmInvocationRequest
        {
            WorkspaceId = "default",
            SessionId = "session_1",
            AgentInstanceId = "agent_1",
            AgentTemplateId = "general-assistant",
            ProfileId = "conscious.default",
            Messages = Array.Empty<ChatMessage>(),
        };

        Assert.IsNotNull(request.Tools);
        Assert.AreEqual(0, request.Tools.Count);
    }

    [TestMethod]
    public void LlmInvocationResult_Default_ToolCalls_Is_Not_Null()
    {
        var result = new LlmInvocationResult
        {
            Success = true,
            ReplyText = "hello",
        };

        Assert.IsNotNull(result.ToolCalls);
        Assert.AreEqual(0, result.ToolCalls.Count);
        Assert.AreEqual("hello", result.ReplyText);
        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public void ContextAssemblyRequest_Required_Fields_Are_Settable()
    {
        var request = new ContextAssemblyRequest
        {
            WorkspaceId = "ws",
            SessionId = "sess",
            AgentInstanceId = "agent",
            AgentTemplateId = "tmpl",
            UserMessage = "hello",
            LlmProfileId = "profile",
            MaxContextTokens = 8192,
        };

        Assert.AreEqual("ws", request.WorkspaceId);
        Assert.AreEqual("sess", request.SessionId);
        Assert.AreEqual("agent", request.AgentInstanceId);
        Assert.AreEqual("tmpl", request.AgentTemplateId);
        Assert.AreEqual("hello", request.UserMessage);
        Assert.AreEqual("profile", request.LlmProfileId);
        Assert.AreEqual(8192, request.MaxContextTokens);
    }

    [TestMethod]
    public void ContextAssemblyResult_Layers_Are_Settable()
    {
        var layers = new List<ContextLayerSummary>
        {
            new() { Layer = "system", EstimatedTokens = 100, ItemCount = 1, Source = "prompt" },
            new() { Layer = "user", EstimatedTokens = 50, ItemCount = 1, Source = "message" },
        };

        var result = new ContextAssemblyResult
        {
            Messages = new List<ChatMessage> { new(ChatRole.User, "hello") },
            EstimatedTokens = 150,
            Layers = layers,
            CompactionMode = "gentle",
            MemoryRecallMode = "vector",
        };

        Assert.AreEqual(150, result.EstimatedTokens);
        Assert.AreEqual(2, result.Layers.Count);
        Assert.AreEqual("gentle", result.CompactionMode);
        Assert.AreEqual("vector", result.MemoryRecallMode);
        Assert.AreEqual("system", result.Layers[0].Layer);
        Assert.AreEqual(100, result.Layers[0].EstimatedTokens);
    }

    [TestMethod]
    public void ToolInvocationRequest_Required_Fields_Are_Settable()
    {
        var request = new ToolInvocationRequest
        {
            WorkspaceId = "ws",
            SessionId = "sess",
            AgentInstanceId = "agent",
            ToolCallId = "call_001",
            ToolName = "read_file",
            ArgumentsJson = "{\"path\":\"/tmp\"}",
        };

        Assert.AreEqual("call_001", request.ToolCallId);
        Assert.AreEqual("read_file", request.ToolName);
        Assert.AreEqual("{\"path\":\"/tmp\"}", request.ArgumentsJson);
    }

    [TestMethod]
    public void ToolInvocationResult_Defaults()
    {
        var result = new ToolInvocationResult
        {
            Success = false,
            ToolCallId = "call_001",
            ToolName = "write_file",
            Error = "permission denied",
            DurationMs = 5,
        };

        Assert.IsFalse(result.Success);
        Assert.AreEqual("", result.ArgsHash);
        Assert.AreEqual(0, result.OutputLength);
        Assert.AreEqual("permission denied", result.Error);
        Assert.AreEqual(5, result.DurationMs);
    }

    [TestMethod]
    public void SubAgentInvocationRequest_Defaults_IsAsync()
    {
        var request = new SubAgentInvocationRequest
        {
            ParentSessionId = "parent",
            WorkspaceId = "ws",
            ParentAgentInstanceId = "agent",
            TemplateId = "code-agent",
            Task = "review file",
        };

        Assert.IsFalse(request.IsAsync);
        Assert.AreEqual("parent", request.ParentSessionId);
        Assert.AreEqual("code-agent", request.TemplateId);
        Assert.AreEqual("review file", request.Task);
    }

    [TestMethod]
    public void SubAgentInvocationResult_Required_Fields()
    {
        var result = new SubAgentInvocationResult
        {
            SubSessionId = "sub_1",
            RunId = "run_1",
            Status = "completed",
            Reply = "done",
        };

        Assert.AreEqual("sub_1", result.SubSessionId);
        Assert.AreEqual("run_1", result.RunId);
        Assert.AreEqual("completed", result.Status);
        Assert.AreEqual("done", result.Reply);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public void LlmInvocationResult_Error_Path()
    {
        var result = new LlmInvocationResult
        {
            Success = false,
            Error = "rate limit exceeded",
        };

        Assert.IsFalse(result.Success);
        Assert.AreEqual("rate limit exceeded", result.Error);
        Assert.IsNull(result.ReplyText);
        Assert.AreEqual(0, result.ToolCalls.Count);
    }
}
