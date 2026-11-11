using Microsoft.Extensions.DependencyInjection;
using Pudding.ToolPlugins.Contracts;
using Pudding.ToolPlugins.Runtime;
using PuddingCode.Platform;
using PuddingCode.Tools;
using PuddingRuntime.Services.Plugins;
using PuddingRuntime.Services.Tools;

namespace PuddingToolPlugins.IntegrationTests;

/// <summary>
/// A declarative host package exports operation names the product already registered; mounting one executes
/// nothing. It therefore mounts on first use instead of requiring an explicit plugin_load after every host
/// restart. Authorization is unchanged: every call still passes the agent capability policy.
/// </summary>
[TestClass]
public sealed class HostToolAutoMountTests
{
    private const string ExportId = "auto.mount.probe";
    private const string ContentHash = "AUTOMOUNT-PROBE-HASH";
    private static readonly PluginScope Scope = new("workspace", "agent", "session");

    [TestMethod]
    public async Task DeclarativePackageMountsOnFirstUse_UnloadPinsItOff_AndLoadRestoresIt()
    {
        using var provider = Build();
        var host = new HostToolContributions(provider, [Package()]);

        Assert.HasCount(1, host.ListTools(Context([ExportId])), "first enumeration must mount the package without plugin_load");
        Assert.HasCount(1, host.ListTools(Context([ExportId])), "auto-mount is idempotent");

        var pinned = host.Load(ContentHash, Scope, "probe-load", null, true, Context([ExportId]));
        Assert.AreEqual("Active", pinned.State);
        await host.UnloadAsync(pinned.ActivationId, Scope, null, default);
        Assert.HasCount(0, host.ListTools(Context([ExportId])), "an explicit unload must pin the package off");

        var restored = host.Load(ContentHash, Scope, "probe-reload", null, true, Context([ExportId]));
        Assert.AreEqual("Active", restored.State);
        Assert.HasCount(1, host.ListTools(Context([ExportId])), "an explicit load clears the pin");
    }

    [TestMethod]
    public void PolicyDenialKeepsThePackageUnmounted()
    {
        using var provider = Build();
        var host = new HostToolContributions(provider, [Package()]);

        Assert.HasCount(0, host.ListTools(Context(["some.other.tool"])));
    }

    private static HostToolPackage Package() => new(
        "pudding.probe",
        "1.0.0",
        new Dictionary<string, string>(StringComparer.Ordinal) { [ExportId] = typeof(ProbeTool).FullName! },
        ContentHash);

    private static ToolExecutionContext Context(string[] allowed) => new()
    {
        WorkspaceId = "workspace",
        SessionId = "session",
        AgentInstanceId = "agent",
        CapabilityPolicy = new CapabilityPolicy { AllowedToolNames = allowed },
    };

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IToolPermissionPolicyService>(new AllowAllPolicy());
        HostToolContributions.Register(services);
        services.AddPuddingTool<ProbeTool>();
        return services.BuildServiceProvider();
    }

    public sealed class ProbeTool : IPuddingTool
    {
        public ToolDescriptor Descriptor => new() { ToolId = ExportId, Name = "Probe", Description = "Auto-mount probe" };

        public Task<ToolExecutionResult> ExecuteAsync(ToolExecutionRequest request, CancellationToken ct = default)
            => Task.FromResult(new ToolExecutionResult { Success = true });
    }

    private sealed class AllowAllPolicy : IToolPermissionPolicyService
    {
        public ToolPermissionDecision Classify(ToolDescriptor descriptor) => throw new NotSupportedException();

        public bool RequiresRuntimeAuthorization(ToolDescriptor descriptor) => false;

        public bool CanExposeToAgent(ToolDescriptor descriptor, CapabilityPolicy? policy) => true;

        public CapabilityPolicy BuildCapabilityPolicy(
            IEnumerable<ToolDescriptor> descriptors,
            IEnumerable<string> selectedToolNames,
            bool isTaskRole) => throw new NotSupportedException();
    }
}
