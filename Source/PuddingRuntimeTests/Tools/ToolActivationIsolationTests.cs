using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// Guards the activation-isolation contract: one tool that cannot be constructed from the
/// container must not remove every other tool from the registry, and the gap must be observable
/// instead of silently swallowed.
/// </summary>
[TestClass]
public sealed class ToolActivationIsolationTests
{
    /// <summary>Never registered — the deliberate activation gap.</summary>
    public interface IIsolationMissingDependency
    {
        string Value { get; }
    }

    [Tool(id: "isolation_needs_missing",
          name: "Isolation needs missing dependency",
          description: "Cannot be constructed: its dependency is not registered.",
          permission: ToolPermissionLevel.Low)]
    public sealed class NeedsMissingDependencyTool : PuddingToolBase<object>
    {
        public NeedsMissingDependencyTool(IIsolationMissingDependency dependency)
        {
            _ = dependency;
        }

        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct) =>
            Task.FromResult(ToolExecutionResult.Ok("unreachable"));
    }

    [Tool(id: "isolation_healthy",
          name: "Isolation healthy",
          description: "Constructible with no dependencies.",
          permission: ToolPermissionLevel.Low)]
    public sealed class HealthyIsolationTool : PuddingToolBase<object>
    {
        protected override Task<ToolExecutionResult> ExecuteCoreAsync(
            object args,
            ToolExecutionContext context,
            CancellationToken ct) =>
            Task.FromResult(ToolExecutionResult.Ok("healthy"));
    }

    [TestMethod]
    public void Registry_BuildsAndKeepsHealthyTools_WhenOneToolCannotActivate()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<NeedsMissingDependencyTool>();
        services.AddPuddingTool<HealthyIsolationTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();
        var diagnostics = provider.GetRequiredService<IToolActivationDiagnostics>();

        Assert.IsNotNull(
            registry.GetTool("isolation_healthy"),
            "A tool whose dependency is missing must not remove unrelated tools from the registry.");
        Assert.IsNull(
            registry.GetTool("isolation_needs_missing"),
            "The tool that cannot activate must not be exposed.");

        var failure = diagnostics.Failures.SingleOrDefault(
            f => f.ToolType.EndsWith(nameof(NeedsMissingDependencyTool), StringComparison.Ordinal));

        Assert.IsNotNull(
            failure,
            "The skipped tool must be reported through IToolActivationDiagnostics instead of vanishing.");
        Assert.IsTrue(
            failure!.Message.Contains(nameof(IIsolationMissingDependency), StringComparison.Ordinal),
            $"Diagnostics must keep the original container error so the missing dependency is identifiable. Actual: {failure.Message}");
    }

    [TestMethod]
    public void ActivationFailure_IsRecordedOnce_EvenThoughTheRegistryRebuildsItsSnapshot()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<NeedsMissingDependencyTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();
        var diagnostics = provider.GetRequiredService<IToolActivationDiagnostics>();

        // Every lookup rebuilds the snapshot, so the broken tool is offered repeatedly.
        _ = registry.ListDescriptors();
        _ = registry.ListAvailable(policy: null);
        _ = registry.GetTool("isolation_needs_missing");

        Assert.AreEqual(
            1,
            diagnostics.Failures.Count(f => f.ToolType.EndsWith(nameof(NeedsMissingDependencyTool), StringComparison.Ordinal)),
            "Failures are de-duplicated by tool type: the sink records which tools are unreachable, not how often the registry looked.");
    }

    [TestMethod]
    public void HealthyTool_ResolvesToTheSameInstanceAsTheContainerService()
    {
        var services = new ServiceCollection();
        services.AddPuddingTool<HealthyIsolationTool>();
        services.AddPuddingToolRegistry();

        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IPuddingToolRegistry>();

        Assert.AreSame(
            provider.GetRequiredService<HealthyIsolationTool>(),
            registry.GetTool("isolation_healthy"),
            "Activation must reuse the container's singleton so tool identity stays consistent.");
    }
}
