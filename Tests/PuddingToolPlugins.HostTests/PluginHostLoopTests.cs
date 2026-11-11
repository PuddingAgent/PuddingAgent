using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Windows;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingCode.Tools;
using PuddingRuntime.Services;
using PuddingRuntime.Services.AgentLoop;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Skills;
using PuddingRuntime.Services.Tools;

namespace PuddingToolPlugins.HostTests;

[TestClass]
public sealed class PluginHostLoopTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SameAgentLoop_LoadsInvokesUnloadsRealDll_AndRevokesNextSchema(bool streaming)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(), "host-plugins-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var worker = Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER");
        Assert.IsTrue(File.Exists(worker), "Build the worker and set PUDDING_PLUGIN_WORKER to its exe.");
        await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root), new WindowsPluginWorkerFactory(worker!));
        try
        {
            var package = await Import(plugins);
            var pid = Environment.ProcessId;
            var firewall = new Mock<IAgentFirewall>();
            firewall.Setup(f => f.EvaluateAsync(It.IsAny<FirewallContext>(), It.IsAny<CancellationToken>())).ReturnsAsync(FirewallDecision.Allow());
            // Explicit test authorization. Registry visibility still uses the real policy service.
            var registry = new PuddingToolRegistry([new PluginLoadTool(plugins), new PluginUnloadTool(plugins)],
                contextualToolSources: [plugins]);
            var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
            var executor = new PuddingToolExecutionService(registry, sandbox, NullLogger<PuddingToolExecutionService>.Instance, firewall: firewall.Object);
            var llm = new LoopClient(plugins, package.ContentHash);
            var service = BuildLoop(llm, new ToolInvocationService(executor), new PuddingToolSchemaService(registry), [new ExecutablePluginHook(plugins)], root);
            var request = new RuntimeDispatchRequest
            {
                SessionId = "session", AgentInstanceId = "agent", WorkspaceId = "workspace", AgentTemplateId = "test",
                WorkingDirectory = root, MessageText = "Load the plugin, echo Chinese text, unload it, and verify removal.",
                MaxRounds = 5, SuppressContextAutoCompaction = true,
                CapabilityPolicy = new() { DefaultToolNames = ["plugin_unload"], RequiresGrantToolNames = ["plugin_load"], AllowShellExecution = true },
                LlmConfig = new() { ModelId = "test-model", MaxContextTokens = 16384 },
            };
            if (streaming) { await foreach (var _ in service.ExecuteStreamAsync(request)) { } }
            else await service.ExecuteAsync(request);
            Assert.AreEqual(4, llm.Calls, "Must complete load → call → unload → final in one dispatch.");
            Assert.IsTrue(llm.SawEchoEvidence, "The model must observe the actual plugin result.");
            Assert.AreEqual("Unloaded", plugins.Manager.List().Single().State);
            Assert.AreEqual(pid, Environment.ProcessId);
            Assert.AreEqual(0, registry.ListAvailable(null, Context("other-agent")).Count(t => t.SourceKind == "Plugin"));
            firewall.Verify(f => f.EvaluateAsync(It.IsAny<FirewallContext>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        }
        finally
        {
            await plugins.DisposeAsync();
            for (var attempt = 0; ; attempt++)
                try { Directory.Delete(root, true); break; }
                catch (Exception e) when (attempt < 20 && e is IOException or UnauthorizedAccessException) { await Task.Delay(50); }
        }
    }

    [TestMethod]
    public async Task ScopedDescriptor_ReachesFirewallCapabilityGate()
    {
        var source = new ScopedSource();
        var registry = new PuddingToolRegistry([], contextualToolSources: [source]);
        var firewall = new AgentFirewall(toolRegistry: registry, policySvc: new ToolPermissionPolicyService());
        var context = FirewallContext.FromExecutionContext(Context("agent"), new CapabilityPolicy { AllowedToolNames = ["some_other_tool"] }, toolId: "scoped_tool");
        var result = await firewall.EvaluateAsync(context, default);
        Assert.IsFalse(result.Allowed, "A context-only tool must not bypass capability checks through a global descriptor lookup.");
        Assert.IsNull(registry.GetTool("scoped_tool", Context("other-agent")));
    }

    [TestMethod]
    public async Task FailedWorkerStart_IsFailedToolResult_WithStructuredDiagnosis()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_TEST_ROOT") ?? Path.GetTempPath(), "plugin-failed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var factory = new Mock<IPluginWorkerFactory>();
        factory.Setup(f => f.StartAsync(It.IsAny<PluginPackage>(), It.IsAny<PluginScope>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PluginException("plugin.worker_missing", "spawn", "Injected missing worker."));
        await using var plugins = new ExecutablePluginService(PuddingDataPaths.FromRoot(root), factory.Object);
        try
        {
            var package = await Import(plugins);
            var result = await new PluginLoadTool(plugins).ExecuteAsync(new() { Context = Context("agent"), ToolCallId = "failed-load",
                ArgumentsJson = JsonSerializer.Serialize(new { contentHash = package.ContentHash, operationId = "failed", enableHooks = true }) });
            Assert.IsFalse(result.Success); Assert.AreEqual(1, result.ExitCode);
            Assert.AreEqual("plugin.worker_missing", result.Status);
            using var body = JsonDocument.Parse(result.Output);
            Assert.AreEqual("Failed", body.RootElement.GetProperty("state").GetString());
            Assert.AreEqual("spawn", body.RootElement.GetProperty("failure").GetProperty("stage").GetString());
            Assert.AreEqual(0, plugins.ListTools(Context("agent")).Count);
        }
        finally { await plugins.DisposeAsync(); Directory.Delete(root, true); }
    }

    /// <summary>
    /// These tests drive a real worker process. Without the acceptance environment they must skip like
    /// the sibling classes in PuddingToolPlugins.IntegrationTests, instead of failing on a missing exe
    /// (measured 2026-11-12: the commit gate now really runs Microsoft.Testing.Platform projects).
    /// </summary>
    [TestInitialize]
    public void RequirePluginWorker()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUDDING_PLUGIN_WORKER")))
            Assert.Inconclusive("Set PUDDING_PLUGIN_WORKER to a built plugin worker executable (plugin acceptance environment).");
    }

    private static ToolExecutionContext Context(string agent) => new() { WorkspaceId = "workspace", SessionId = "session", AgentInstanceId = agent };
    [TestMethod]
    public void DynamicContributions_RequireTheirHostAssignedOwner_AndKeepInvocationRisk()
    {
        var policy = new ToolPermissionPolicyService();
        var descriptor = new ToolDescriptor { ToolId = "generation_tool", Name = "Write", Description = "Write", PermissionOwnerToolId = "plugin_load",
            PermissionLevel = ToolPermissionLevel.High, Safety = ToolSafetyFlags.RequiresFileWrite };
        Assert.IsTrue(policy.CanExposeToAgent(descriptor, new CapabilityPolicy { RequiresGrantToolNames = ["plugin_load"] }));
        Assert.IsTrue(policy.RequiresRuntimeAuthorization(descriptor));
        Assert.IsFalse(policy.CanExposeToAgent(descriptor, new CapabilityPolicy()));
        Assert.IsFalse(policy.CanExposeToAgent(descriptor, new CapabilityPolicy { AllowedToolNames = ["file_read"] }));
        Assert.IsTrue(policy.CanExposeToAgent(descriptor, new CapabilityPolicy { AllowedToolNames = ["plugin_load"] }));
    }
    [TestMethod]
    public async Task ScopedEffects_RespectResourcePolicy_WithoutHardCodedToolNames()
    {
        var registry = new PuddingToolRegistry([], contextualToolSources: [new ScopedSource()]);
        var firewall = new AgentFirewall(toolRegistry: registry, policySvc: new ToolPermissionPolicyService());
        var denied = await firewall.EvaluateAsync(FirewallContext.FromExecutionContext(Context("agent"),
            new CapabilityPolicy { AllowedToolNames = ["scoped_tool"], AllowNetworkAccess = false }, toolId: "scoped_tool"), default);
        Assert.IsFalse(denied.Allowed);
        StringAssert.Contains(denied.DenyReason!, "network");
    }
    private sealed class ScopedSource : IContextualPuddingToolSource
    {
        public long Revision => 1;
        public IReadOnlyList<IPuddingTool> ListTools(ToolExecutionContext context) => context.AgentInstanceId == "agent" ? [new ScopedTool()] : [];
    }
    private sealed class ScopedTool : IPuddingTool
    {
        public ToolDescriptor Descriptor => new() { ToolId = "scoped_tool", Name = "Scoped", Description = "Test", PermissionLevel = ToolPermissionLevel.High, Safety = ToolSafetyFlags.RequiresNetwork };
        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default) => throw new AssertFailedException("Denied tool executed.");
    }
    private static async Task<PluginPackage> Import(ExecutablePluginService plugins)
    {
        var manifest = new PluginManifest
        {
            Id = "sample.fixture", Name = "Fixture", Version = "1.0.0", Entry = new("bin/Sample.PluginFixture.dll", "Sample.PluginFixture.FixturePlugin"),
            Hooks = [new("advice", "tool.result.advice")],
            Tools = new[] { "echo", "read_file", "hang", "flood", "crash", "child_hang" }.Select(id => new PluginToolDefinition
            {
                Id = id, Name = id, Description = "Fixture " + id,
                InputSchema = PluginJson.Value(new { type = "object", properties = new { text = new { type = "string" } }, required = new[] { "text" }, additionalProperties = false }),
                OutputSchema = PluginJson.Value(new { type = "string" }),
            }).ToArray(),
        };
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("plugin.json").Open())) writer.Write(JsonSerializer.Serialize(manifest, PluginJson.Options));
            using var output = zip.CreateEntry(manifest.Entry.Assembly).Open();
            using var input = File.OpenRead(typeof(Sample.PluginFixture.FixturePlugin).Assembly.Location); input.CopyTo(output);
        }
        stream.Position = 0; return await plugins.Packages.ImportAsync(stream, default);
    }
    private sealed class LoopClient(ExecutablePluginService plugins, string hash) : IRuntimeLlmClient
    {
        public int Calls; public bool SawEchoEvidence;
        private string? _echo;
        private LlmResponse Next(IReadOnlyList<ChatMessage> messages, IReadOnlyList<LlmToolDefinition>? tools)
        {
            Calls++;
            var names = tools!.Select(t => t.Name).ToArray();
            if (Calls == 1)
            {
                Assert.IsFalse(names.Any(n => n.StartsWith("px_")));
                CollectionAssert.Contains(names, "plugin_load");
                return new(null, [new("load", "plugin_load", JsonSerializer.Serialize(new { contentHash = hash, operationId = "test", approvedCapabilities = Array.Empty<string>(), enableHooks = true }))]);
            }
            if (Calls == 2)
            {
                _echo = plugins.Manager.List().Single().ToolNames.Single(n => n.EndsWith("_echo"));
                CollectionAssert.Contains(names, _echo, "The next provider request must include the freshly loaded schema.");
                return new(null, [new("echo", _echo, "{\"text\":\"插件真实回执\"}")]);
            }
            if (Calls == 3)
            {
                SawEchoEvidence = messages.Any(m => m.Role == ChatRole.Tool && m.ToolCallId == "echo"
                    && (m.Content?.Contains("插件真实回执", StringComparison.Ordinal) == true
                        || m.Content?.Contains("\\u63D2\\u4EF6", StringComparison.OrdinalIgnoreCase) == true));
                return new(null, [new("unload", "plugin_unload", JsonSerializer.Serialize(new { activationId = plugins.Manager.List().Single().ActivationId }))]);
            }
            Assert.AreEqual(4, Calls);
            Assert.IsFalse(names.Any(n => n.StartsWith("px_")), "Unloaded generation must disappear on the next round.");
            return new("{\"status\":\"DONE\",\"message\":\"Plugin returned the requested text and was unloaded; all steps complete.\"}", null);
        }
        public Task<LlmResponse> ChatAsync(string workspaceId, string sessionId, string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null, CancellationToken ct = default) => Task.FromResult(Next(messages, tools));
        public async IAsyncEnumerable<StreamDelta> ChatStreamAsync(string workspaceId, string sessionId, string agentTemplateId, IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<LlmToolDefinition>? tools = null, LlmConfig? llmConfig = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            var response = Next(messages, tools);
            foreach (var call in response.ToolCalls ?? []) yield return new() { ToolCallIndex = 0, ToolCallId = call.Id, ToolCallNameDelta = call.Name, ToolCallArgsDelta = call.ArgumentsJson };
            yield return new() { ContentDelta = response.Content, FinishReason = response.ToolCalls?.Count > 0 ? "tool_calls" : "stop" };
            await Task.CompletedTask;
        }
    }
    private static AgentExecutionService BuildLoop(IRuntimeLlmClient llm, IToolInvocationService tools, PuddingToolSchemaService schemas, IAgentLoopHook[] hooks, string root)
    {
        var sessions = new AgentSessionManager(NullLogger<AgentSessionManager>.Instance);
        var store = new InMemoryRuntimeSessionStore(); var journal = new ExecutionJournal();
        var control = new ExecutionControlRegistry(); var memory = Mock.Of<IMemoryEngine>();
        var sandbox = new SandboxExecutor(NullLogger<SandboxExecutor>.Instance);
        var packages = new AgentSkillPackageRegistry(); var skills = new SkillRuntime([], sandbox, NullLogger<SkillRuntime>.Instance);
        var paths = PuddingDataPaths.FromRoot(root);
        var prompt = new SystemPromptBuilder(memory, skills, packages, NullLogger<SystemPromptBuilder>.Instance, new StartupEnvironmentInfo(), dataPaths: paths);
        var environment = Mock.Of<IExecutionEnvironmentProvider>(e => e.OsDescription == "Windows" && e.OsArchitecture == "X64" && e.RuntimeVersion == "10.0" && e.DefaultShell == "pwsh" && e.AppBaseDirectory == root && e.PathSeparator == "\\" && e.EnvironmentFingerprint == "test");
        var pipeline = new ContextPipeline(memory, skills, packages, prompt, new MemoryCache(new MemoryCacheOptions()), new ContextAssemblyStore(), NullLogger<ContextPipeline>.Instance, environment, dataPaths: paths);
        var manager = new ContextWindowManager(sessions, store, control, journal, NullLogger<ContextWindowManager>.Instance);
        return new(sessions, store, memory, sandbox, llm, skills, new AgentExecutionGuardrails(), control, journal,
            new CompletionPolicy(), packages, new SkillPackageDownloadService(Mock.Of<IHttpClientFactory>(), NullLogger<SkillPackageDownloadService>.Instance), hooks,
            pipeline, manager, NullLogger<AgentExecutionService>.Instance, new SessionExecutionGate(NullLogger<SessionExecutionGate>.Instance),
            toolInvocationService: tools, toolSchemaService: schemas);
    }
}
