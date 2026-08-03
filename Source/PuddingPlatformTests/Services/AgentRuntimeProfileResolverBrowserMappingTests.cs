using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PuddingCode.Abstractions;
using PuddingCode.Agents;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingPlatformTests.Services;

/// <summary>
/// Doc 79 Section 4.3: capability ↔ browser tool mapping tests.
/// Freezes the seven-item browser capability/tool contract so silent drift is impossible.
/// </summary>
[TestClass]
public sealed class AgentRuntimeProfileResolverBrowserMappingTests
{
    // ── Frozen contract (Doc 79 §4.2) ──────────────────────────────────────

    private static readonly string[] BrowserToolIds =
    [
        "browser_context",
        "browser_tabs",
        "browser_navigate",
        "browser_snapshot",
        "browser_locate",
        "browser_interact",
        "browser_wait_for",
    ];

    private static readonly string[] BrowserCapabilityIds =
    [
        "cap-browser-context",
        "cap-browser-tabs",
        "cap-browser-navigate",
        "cap-browser-snapshot",
        "cap-browser-locate",
        "cap-browser-interact",
        "cap-browser-wait-for",
    ];

    // ── Factory helpers ────────────────────────────────────────────────────

    private static AgentRuntimeProfileResolver CreateResolver(
        string dataRoot,
        IWorkspaceAgentCatalog? agentCatalog = null,
        IPuddingToolCatalogService? toolCatalog = null)
    {
        var paths = PuddingDataPaths.FromRoot(dataRoot);
        return new AgentRuntimeProfileResolver(
            agentCatalog ?? new StubAgentCatalog(MakeAgent("agent-default", "default")),
            new AgentProfileProvider(paths),
            CreateDefaultLlmConfigService(),
            null! /* db – unused when SkillPackageIds is empty */,
            null! /* minio – unused when SkillPackageIds is empty */,
            toolCatalog ?? CreateBrowserToolCatalog(),
            new ToolPermissionPolicyService(),
            NullLogger<AgentRuntimeProfileResolver>.Instance);
    }

    private static ILlmConfigService CreateDefaultLlmConfigService()
        => new PuddingFileLlmConfigService(new PuddingLlmProvidersConfig
        {
            Providers =
            [
                new PuddingLlmProviderConfig
                {
                    ProviderId = "qwen",
                    Name = "Qwen",
                    BaseUrl = "https://example.invalid/v1",
                    IsEnabled = true,
                    Models =
                    [
                        new PuddingLlmModelConfig
                        {
                            ModelId = "qwen-max",
                            Name = "Qwen Max",
                            IsDeprecated = false,
                        },
                    ],
                },
            ],
        });

    private static IPuddingToolCatalogService CreateBrowserToolCatalog()
        => new InMemoryToolCatalog(BrowserToolIds.Select(id => new ToolDescriptor
        {
            ToolId = id,
            Name = id,
            Description = $"Browser tool: {id}",
        }));

    // ── Manifest helpers ───────────────────────────────────────────────────

    private static async Task WriteManifestAsync(string agentRoot, AgentInstanceManifest manifest, CancellationToken ct = default)
    {
        Directory.CreateDirectory(agentRoot);
        var json = JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
            });
        await File.WriteAllTextAsync(Path.Combine(agentRoot, "manifest.json"), json, ct);
    }

    private static async Task WriteAllBrowserCapManifestAsync(string agentRoot, CancellationToken ct = default)
    {
        await WriteManifestAsync(agentRoot, new AgentInstanceManifest
        {
            AgentInstanceId = "agent-browser-mapping-test",
            TemplateId = "general-assistant",
            WorkspaceId = "workspace-test",
            PreferredProviderId = "qwen",
            PreferredModelId = "qwen-max",
            Role = "Task",
            Capabilities = new AgentCapabilitiesConfig
            {
                AllowedToolIds = [.. BrowserCapabilityIds],
                AllowFileWrite = false,
                AllowShellExecution = false,
                AllowNetworkAccess = false,
                AllowedToolNames = [],
            },
            SkillPackageIds = [],
        }, ct);
    }

    private static async Task WriteNoCapManifestAsync(string agentRoot, CancellationToken ct = default)
    {
        await WriteManifestAsync(agentRoot, new AgentInstanceManifest
        {
            AgentInstanceId = "agent-no-browser-cap",
            TemplateId = "general-assistant",
            WorkspaceId = "workspace-test",
            PreferredProviderId = "qwen",
            PreferredModelId = "qwen-max",
            Role = "Service",
            Capabilities = new AgentCapabilitiesConfig
            {
                AllowedToolIds = [],
                AllowFileWrite = false,
                AllowShellExecution = false,
                AllowNetworkAccess = false,
                AllowedToolNames = [],
            },
            SkillPackageIds = [],
        }, ct);
    }

    private static async Task WriteUnknownCapManifestAsync(string agentRoot, CancellationToken ct = default)
    {
        await WriteManifestAsync(agentRoot, new AgentInstanceManifest
        {
            AgentInstanceId = "agent-unknown-cap",
            TemplateId = "general-assistant",
            WorkspaceId = "workspace-test",
            PreferredProviderId = "qwen",
            PreferredModelId = "qwen-max",
            Role = "Service",
            Capabilities = new AgentCapabilitiesConfig
            {
                AllowedToolIds = ["cap-browser-unknown"],
                AllowFileWrite = false,
                AllowShellExecution = false,
                AllowNetworkAccess = false,
                AllowedToolNames = [],
            },
            SkillPackageIds = [],
        }, ct);
    }

    // ── Agent DTO factory ──────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static WorkspaceAgentDto MakeAgent(string agentId, string name, string? displayName = null)
        => new(
            AgentId: agentId,
            Name: name,
            Description: null,
            DisplayName: displayName ?? agentId,
            AvatarId: null,
            AvatarUrl: null,
            SourceTemplateId: null,
            MainSessionId: null,
            SystemPromptOverride: null,
            PreferredProviderId: null,
            PreferredModelId: null,
            IsEnabled: true,
            IsFrozen: false,
            CreatedAt: Now,
            UpdatedAt: Now);

    // ════════════════════════════════════════════════════════════════════════
    // Test 1: All browser capabilities → seven tool definitions
    // ════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AgentWithAllBrowserCapabilities_ResolvesSevenBrowserToolDefinitions()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-browser-mapping-test");
        await WriteAllBrowserCapManifestAsync(agentRoot);

        var resolver = CreateResolver(temp.Path,
            agentCatalog: new StubAgentCatalog(MakeAgent("agent-browser-mapping-test", "browser-mapping-test")));

        var profile = await resolver.ResolveAsync("workspace-test", "agent-browser-mapping-test");

        // Set equality, not just count
        Assert.IsNotNull(profile.ToolDefinitions, "ToolDefinitions should not be null");
        var resolved = profile.ToolDefinitions.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = BrowserToolIds.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual(expected.Count, resolved.Count,
            $"Expected {expected.Count} tools, got {resolved.Count}: [{string.Join(", ", resolved.OrderBy(n => n))}]");
        foreach (var id in expected)
            Assert.IsTrue(resolved.Contains(id), $"Missing tool '{id}'. Resolved: [{string.Join(", ", resolved.OrderBy(n => n))}]");

        Assert.IsNotNull(profile.CapabilityPolicy, "CapabilityPolicy should not be null");
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 2: No browser capability → no browser tools
    // ════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AgentWithoutBrowserCapabilities_DoesNotResolveBrowserTools()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-no-browser-cap");
        await WriteNoCapManifestAsync(agentRoot);

        var resolver = CreateResolver(temp.Path,
            agentCatalog: new StubAgentCatalog(MakeAgent("agent-no-browser-cap", "no-browser-cap")));

        var profile = await resolver.ResolveAsync("workspace-test", "agent-no-browser-cap");

        if (profile.ToolDefinitions is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
                Assert.IsFalse(tool.Name.StartsWith("browser_", StringComparison.OrdinalIgnoreCase),
                    $"Agent without browser caps should not have '{tool.Name}'.");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 3: Unknown capability ignored — no privilege escalation
    // ════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UnknownBrowserCapability_IsIgnoredWithoutGrantingAnotherTool()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-unknown-cap");
        await WriteUnknownCapManifestAsync(agentRoot);

        var resolver = CreateResolver(temp.Path,
            agentCatalog: new StubAgentCatalog(MakeAgent("agent-unknown-cap", "unknown-cap")));

        var profile = await resolver.ResolveAsync("workspace-test", "agent-unknown-cap");

        if (profile.ToolDefinitions is { Count: > 0 } tools)
        {
            foreach (var tool in tools)
                Assert.IsFalse(tool.Name.StartsWith("browser_", StringComparison.OrdinalIgnoreCase),
                    $"Unknown cap should not grant browser tool '{tool.Name}'.");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Test 4: Capability IDs round-trip — preserves selected capabilities
    // ════════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task BrowserCapabilityRoundTrip_PreservesSelectedCapabilityIds()
    {
        using var temp = new TempDirectory();
        var paths = PuddingDataPaths.FromRoot(temp.Path);
        var agentRoot = paths.AgentInstanceRoot("agent-browser-mapping-test");
        await WriteAllBrowserCapManifestAsync(agentRoot);

        var resolver = CreateResolver(temp.Path,
            agentCatalog: new StubAgentCatalog(MakeAgent("agent-browser-mapping-test", "browser-mapping-test")));

        var profile = await resolver.ResolveAsync("workspace-test", "agent-browser-mapping-test");

        Assert.IsNotNull(profile.ToolDefinitions, "ToolDefinitions should not be null");
        var names = profile.ToolDefinitions.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.AreEqual(BrowserToolIds.Length, names.Count,
            "Round-trip should preserve exactly seven browser tool definitions.");

        for (var i = 0; i < BrowserCapabilityIds.Length; i++)
        {
            Assert.IsTrue(names.Contains(BrowserToolIds[i]),
                $"Capability '{BrowserCapabilityIds[i]}' should resolve to '{BrowserToolIds[i]}'. " +
                $"Resolved: [{string.Join(", ", names.OrderBy(n => n))}]");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Mocks and stubs
    // ════════════════════════════════════════════════════════════════════════

    private sealed class StubAgentCatalog(WorkspaceAgentDto agent) : IWorkspaceAgentCatalog
    {
        public Task<IReadOnlyList<WorkspaceAgentDto>> ListAgentsAsync(string workspaceId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<WorkspaceAgentDto>>(new[] { agent });
    }

    private sealed class InMemoryToolCatalog(IEnumerable<ToolDescriptor> descriptors) : IPuddingToolCatalogService
    {
        private readonly IReadOnlyList<ToolDescriptor> _descriptors = descriptors.ToList();
        public IReadOnlyList<ToolDescriptor> ListTools(bool enabledByDefaultOnly = false) => _descriptors;
        public IReadOnlyList<ToolDescriptor> ListTools(string workspaceId, bool enabledByDefaultOnly = false) => _descriptors;
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "pudding-browser-mapping-tests",
            Guid.NewGuid().ToString("N"));

        public TempDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
