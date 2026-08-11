using System.Text.Json;
using PuddingCode.Abstractions;
using PuddingCode.Orchestration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingRuntime.Services.Orchestration;

namespace PuddingRuntimeTests.Services.Orchestration;

[TestClass]
public sealed class SubAgentOrchestrationNodeExecutorTests
{
    [TestMethod]
    public async Task Execute_UsesExactRouteAndCommitsReplyToResultPort()
    {
        var invocation = new RecordingSubAgentInvocationService();
        var configs = new FakeLlmConfigService();
        configs.Routes["opencode/kimi-k3"] = new LlmConfig
        {
            Endpoint = "https://example.invalid/v1",
            ModelId = "kimi-k3",
            Protocol = "responses"
        };
        var executor = new SubAgentOrchestrationNodeExecutor(invocation, configs);
        var context = CreateContext();

        var result = await executor.ExecuteAsync(context, CancellationToken.None);

        Assert.AreEqual("child-run-1", result.ExecutionRunId);
        Assert.AreEqual("child-session-1", result.SubSessionId);
        Assert.AreEqual("镜头一：蓝色茶壶在雨夜发光。", result.Outputs["result"].InlineValue?.GetString());
        Assert.AreEqual("opencode", invocation.Request!.LlmProfile.ProviderId);
        Assert.AreEqual("kimi-k3", invocation.Request.LlmProfile.ModelId);
        Assert.AreEqual("storyboard-director", invocation.Request.RoleInPlan);
        StringAssert.StartsWith(invocation.Request.ParentAgentInstanceId, "orchestration-");
        Assert.IsFalse(
            invocation.Request.ParentAgentInstanceId.Contains(':'),
            "Audit principal delimiters must never leak into the filesystem-backed Agent identity.");
        Assert.IsNull(invocation.Request.MaxRounds, "Orchestration must keep numeric Agent budgets system-owned.");
        StringAssert.Contains(invocation.Request.Task, "将策划案转换成一个可直接生成图片的镜头文案");
        StringAssert.Contains(invocation.Request.Task, "制作一张蓝色茶壶海报");
    }

    private static AgentOrchestrationNodeExecutionContext CreateContext()
    {
        var node = new AgentOrchestrationNodeDefinition
        {
            NodeId = "storyboard",
            Kind = AgentOrchestrationNodeKind.SubAgent,
            Title = "分镜策划",
            Objective = "将策划案转换成一个可直接生成图片的镜头文案。",
            Component = new AgentOrchestrationComponentReference
            {
                ComponentType = AgentOrchestrationComponentTypes.SubAgent,
                Version = "1"
            },
            Executor = new AgentOrchestrationExecutorBinding
            {
                Kind = AgentOrchestrationExecutorKind.SubAgent,
                Role = "storyboard-director",
                TemplateId = "general-assistant",
                RouteKey = "opencode/kimi-k3"
            },
            GraphInputBindings =
            [
                new AgentOrchestrationGraphInputBinding { InputId = "brief", TargetPortId = "request" }
            ],
            ExpectedOutputContract = AgentOrchestrationDataTypes.Content,
            PermissionMode = AgentOrchestrationPermissionMode.ReadOnly
        };
        var definition = new AgentOrchestrationGraphDefinition
        {
            GraphId = "agent-chain",
            RevisionId = "agent-chain/r001",
            WorkspaceId = "default",
            RootSessionId = "root-session",
            CreatedByAgentId = "admin",
            Objective = "Plan an image.",
            Nodes = [node]
        };
        return new AgentOrchestrationNodeExecutionContext
        {
            Definition = definition,
            Node = node,
            Run = new AgentOrchestrationRunSnapshot
            {
                RunId = "run-agent-chain",
                GraphId = definition.GraphId,
                RevisionId = definition.RevisionId,
                WorkspaceId = definition.WorkspaceId,
                RootSessionId = definition.RootSessionId,
                RequestedByAgentId = "admin",
                Status = AgentOrchestrationRunStatus.Active,
                Inputs = new Dictionary<string, AgentOrchestrationValueEnvelope>
                {
                    ["brief"] = new()
                    {
                        DataType = AgentOrchestrationDataTypes.Content,
                        ContentType = "text/plain",
                        InlineValue = JsonSerializer.SerializeToElement("制作一张蓝色茶壶海报")
                    }
                }
            },
            Claim = new AgentOrchestrationNodeClaim
            {
                RunId = "run-agent-chain",
                NodeId = node.NodeId,
                ClaimId = "claim-storyboard",
                WorkerId = "worker",
                Attempt = 1,
                FencingToken = 1,
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                RunVersion = 2
            }
        };
    }

    private sealed class RecordingSubAgentInvocationService : ISubAgentInvocationService
    {
        public SubAgentInvocationRequest? Request { get; private set; }

        public Task<SubAgentInvocationResult> InvokeAsync(
            SubAgentInvocationRequest request,
            CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new SubAgentInvocationResult
            {
                SubSessionId = "child-session-1",
                RunId = "child-run-1",
                Status = "completed",
                Reply = "镜头一：蓝色茶壶在雨夜发光。"
            });
        }

        public Task<SubAgentBatchInvocationResult> InvokeBatchAsync(
            SubAgentBatchInvocationRequest request,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeLlmConfigService : ILlmConfigService
    {
        public Dictionary<string, LlmConfig> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyList<LlmProviderInfo> GetEnabledProviders() => [];
        public IReadOnlyList<LlmModelInfo> GetAllModels() => [];
        public LlmConfig? Resolve(string providerId, string modelId)
            => Routes.GetValueOrDefault($"{providerId}/{modelId}");
        public LlmProfileInfo? ResolveProfile(string profileId) => null;
        public LlmConfig? GetMemoryConfig() => null;
        public LlmConfig? GetEmbeddingConfig() => null;
        public LlmProviderStrategy? GetProviderStrategy(string providerId) => null;
        public LlmProviderStrategy? GetModelStrategy(string providerId, string modelId) => null;
        public void Reload(object config) { }
    }
}
