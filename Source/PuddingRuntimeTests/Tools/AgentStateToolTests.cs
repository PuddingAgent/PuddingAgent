using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

[TestClass]
public sealed class AgentStateToolTests
{
    [TestMethod]
    public async Task Inspect_UsesCurrentAgentIdentityAndReturnsDiagnostics()
    {
        var service = new RecordingSelfMaintenanceService();
        var tool = CreateTool(service);

        var result = await ExecuteAsync(tool, """{"action":"inspect"}""", "agent-current");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("agent-current", service.LastAgentInstanceId);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.AreEqual("healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.AreEqual("agent-current", doc.RootElement.GetProperty("agentInstanceId").GetString());
    }

    [TestMethod]
    public async Task Read_TruncatesOutputButPreservesContentHash()
    {
        var service = new RecordingSelfMaintenanceService
        {
            Content = "0123456789abcdef",
        };
        var tool = CreateTool(service);

        var result = await ExecuteAsync(
            tool,
            """{"action":"read","document":"memory","max_chars":8}""");

        Assert.IsTrue(result.Success, result.Error);
        using var doc = JsonDocument.Parse(result.Output);
        Assert.AreEqual("01234567", doc.RootElement.GetProperty("content").GetString());
        Assert.AreEqual(16, doc.RootElement.GetProperty("originalLength").GetInt32());
        Assert.IsTrue(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.AreEqual("memory-sha", doc.RootElement.GetProperty("sha256").GetString());
    }

    [TestMethod]
    public async Task Update_PassesOptimisticConcurrencyHashToScopedService()
    {
        var service = new RecordingSelfMaintenanceService();
        var tool = CreateTool(service);

        var result = await ExecuteAsync(
            tool,
            """
            {
              "action":"update",
              "document":"soul",
              "content":"new soul",
              "expected_sha256":"old-sha"
            }
            """,
            "agent-a");

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual("agent-a", service.LastAgentInstanceId);
        Assert.AreEqual("soul", service.LastDocument);
        Assert.AreEqual("new soul", service.LastContent);
        Assert.AreEqual("old-sha", service.LastExpectedSha256);
    }

    [TestMethod]
    public async Task Update_MapsStaleHashToConflictWithoutRetrying()
    {
        var service = new RecordingSelfMaintenanceService
        {
            ConflictOnUpdate = true,
        };
        var tool = CreateTool(service);

        var result = await ExecuteAsync(
            tool,
            """{"action":"update","document":"tools","content":"new","expected_sha256":"stale"}""");

        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.ExitCode);
        StringAssert.Contains(result.Error, "changed since it was read");
        Assert.AreEqual(1, service.UpdateCalls);
    }

    [TestMethod]
    public void Descriptor_IsLowRiskAutoAllowedAgentPrivateStateTool()
    {
        var descriptor = CreateTool(new RecordingSelfMaintenanceService()).Descriptor;

        Assert.AreEqual("agent_state", descriptor.ToolId);
        Assert.AreEqual(ToolPermissionLevel.Low, descriptor.PermissionLevel);
        Assert.AreEqual(ToolCategory.FileSystem, descriptor.Category);
        Assert.IsTrue(descriptor.Safety.HasFlag(ToolSafetyFlags.ConcurrencySafe));
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.RequiresFileWrite));
        Assert.IsFalse(descriptor.Safety.HasFlag(ToolSafetyFlags.Destructive));

        var decision = new ToolPermissionPolicyService().Classify(descriptor);
        Assert.AreEqual(ToolPermissionTier.AutoAllowed, decision.Tier);
        Assert.IsFalse(decision.RequiresRuntimeAuthorization);
        StringAssert.Contains(decision.Reason, "agent-private state");
    }

    private static AgentStateTool CreateTool(IAgentSelfMaintenanceService service)
    {
        var provider = new ServiceCollection()
            .AddSingleton(service)
            .BuildServiceProvider();
        return new AgentStateTool(provider, NullLogger<AgentStateTool>.Instance);
    }

    private static Task<ToolExecutionResult> ExecuteAsync(
        AgentStateTool tool,
        string argumentsJson,
        string agentInstanceId = "agent-a")
        => tool.ExecuteAsync(new ToolExecutionRequest
        {
            ToolCallId = "call-agent-state",
            ArgumentsJson = argumentsJson,
            Context = new ToolExecutionContext
            {
                AgentInstanceId = agentInstanceId,
                WorkspaceId = "default",
                SessionId = "session-1",
            },
        });

    private sealed class RecordingSelfMaintenanceService : IAgentSelfMaintenanceService
    {
        public string Content { get; init; } = "content";
        public bool ConflictOnUpdate { get; init; }
        public string? LastAgentInstanceId { get; private set; }
        public string? LastDocument { get; private set; }
        public string? LastContent { get; private set; }
        public string? LastExpectedSha256 { get; private set; }
        public int UpdateCalls { get; private set; }

        public Task<AgentSelfStateSnapshot> InspectAsync(
            string agentInstanceId,
            CancellationToken ct = default)
        {
            LastAgentInstanceId = agentInstanceId;
            return Task.FromResult(new AgentSelfStateSnapshot
            {
                AgentInstanceId = agentInstanceId,
                TemplateId = "general-assistant",
                DisplayName = "Agent",
                IsEnabled = true,
                Documents = [],
                Issues = [],
            });
        }

        public Task<AgentSelfStateDocument> ReadDocumentAsync(
            string agentInstanceId,
            string document,
            CancellationToken ct = default)
        {
            LastAgentInstanceId = agentInstanceId;
            LastDocument = document;
            return Task.FromResult(new AgentSelfStateDocument
            {
                AgentInstanceId = agentInstanceId,
                Document = document,
                FileName = "MEMORY.md",
                Content = Content,
                Sha256 = "memory-sha",
                LastModifiedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<AgentSelfStateUpdateResult> UpdateDocumentAsync(
            string agentInstanceId,
            string document,
            string content,
            string? expectedSha256 = null,
            CancellationToken ct = default)
        {
            UpdateCalls++;
            LastAgentInstanceId = agentInstanceId;
            LastDocument = document;
            LastContent = content;
            LastExpectedSha256 = expectedSha256;
            if (ConflictOnUpdate)
                throw new AgentSelfStateConflictException(document, expectedSha256 ?? "", "actual");

            return Task.FromResult(new AgentSelfStateUpdateResult
            {
                AgentInstanceId = agentInstanceId,
                Document = document,
                FileName = "SOUL.md",
                PreviousSha256 = expectedSha256 ?? "",
                Sha256 = "new-sha",
                Length = content.Length,
            });
        }
    }
}
