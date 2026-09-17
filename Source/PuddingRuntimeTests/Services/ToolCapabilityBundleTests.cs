using PuddingCode.Models;
using PuddingCode.Platform;
using PuddingRuntime.Services;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

[TestClass]
public sealed class ToolCapabilityBundleTests
{
    private static readonly string[] Capabilities = [
        "file_read", "file_search", "search_grep", "file_write", "file_patch",
        "code_explore", "code_symbol_search", "shell", "terminal_start", "terminal_read",
        "terminal_wait", "terminal_input", "terminal_cancel", "git_status", "git_diff", "git_log", "git_add"];

    [TestMethod]
    public void UnclassifiedTask_DoesNotPreloadCapabilityBundles()
    {
        var plan = ToolExposurePlanner.CreatePlan(Catalog());
        CollectionAssert.AreEqual(new[] { "search_tools" }, Names(plan.VisibleTools));
    }

    [TestMethod]
    public void ReadDiscovery_DoesNotExposeEditingOrShellOrGit()
    {
        var plan = ToolExposurePlanner.CreatePlan(Catalog(), Set("FILE_READ"));
        CollectionAssert.AreEquivalent(new[] { "search_tools", "file_read", "file_search", "search_grep" }, Names(plan.VisibleTools));
    }

    [TestMethod]
    public void EditDiscovery_AddsReadDependenciesButNotCodeOrTerminal()
    {
        var plan = ToolExposurePlanner.CreatePlan(Catalog(), Set("file_patch"));
        CollectionAssert.AreEquivalent(new[] { "search_tools", "file_read", "file_search", "search_grep", "file_patch", "file_write" }, Names(plan.VisibleTools));
    }

    [TestMethod]
    public void CodeDiscovery_AddsOnlyCodeAndReadBundle()
    {
        var plan = ToolExposurePlanner.CreatePlan(Catalog(), Set("code_symbol_search"));
        CollectionAssert.AreEquivalent(new[] { "search_tools", "file_read", "file_search", "search_grep", "code_explore", "code_symbol_search" }, Names(plan.VisibleTools));
    }

    [TestMethod]
    public void GitInspection_DoesNotExposeGitMutation()
    {
        var plan = ToolExposurePlanner.CreatePlan(Catalog(), Set("git_status"));
        CollectionAssert.AreEquivalent(new[] { "search_tools", "git_status", "git_diff", "git_log" }, Names(plan.VisibleTools));
    }

    [TestMethod]
    public void TerminalDiscovery_FreezesCurrentRound_ThenKeepsLaterSchemasIdentical()
    {
        var manifest = AgentExecutionService.BuildFrozenToolManifestCore(Catalog(), null, null);
        var visible = manifest.VisibleTools.ToList();
        var initial = Names(visible);
        var loaded = Set("terminal_start");
        // Search results do not mutate the current request or the frozen initial manifest.
        CollectionAssert.AreEqual(initial, Names(manifest.VisibleTools));
        var promoted = AgentExecutionService.PromoteLoadedToolsForNextRound(manifest, loaded, visible);
        Assert.AreEqual(6, promoted.PromotedToolCount);
        CollectionAssert.AreEqual(initial, Names(visible.Take(initial.Length)));
        var hash = CompositionSnapshot.ComputeToolSpecHash(visible);
        loaded.UnionWith(new[] { "terminal_wait", "terminal_read", "terminal_input", "terminal_cancel", "shell" });
        var repeat = AgentExecutionService.PromoteLoadedToolsForNextRound(manifest, loaded, visible);
        Assert.AreEqual(0, repeat.PromotedToolCount);
        Assert.AreEqual(hash, CompositionSnapshot.ComputeToolSpecHash(visible));
        CollectionAssert.AreEqual(initial, Names(manifest.VisibleTools));
    }

    [TestMethod]
    public void RestoredVisibleOrder_KeepsBundleEvenWhenLoadedStateIsEmpty()
    {
        var first = ToolExposurePlanner.CreatePlan(Catalog(), Set("terminal_start"));
        var previous = Names(first.VisibleTools);
        var restored = AgentExecutionService.BuildFrozenToolManifestCore(Catalog(), null, Set(), previous);
        CollectionAssert.AreEqual(previous, Names(restored.VisibleTools));
        Assert.AreEqual(CompositionSnapshot.ComputeToolSpecHash(first.VisibleTools), CompositionSnapshot.ComputeToolSpecHash(restored.VisibleTools));
    }

    [TestMethod]
    public void RevokedTools_AreNeverReintroducedByBundleOrRequestDefinitions()
    {
        var oldCatalog = Catalog();
        var first = ToolExposurePlanner.CreatePlan(oldCatalog, Set("terminal_start"));
        var allowed = oldCatalog.Where(t => t.Name != "shell" && t.Name != "terminal_input").ToArray();
        var next = AgentExecutionService.BuildFrozenToolManifestCore(allowed, oldCatalog, Set("terminal_start"), Names(first.VisibleTools));
        Assert.IsFalse(next.VisibleTools.Any(t => t.Name is "shell" or "terminal_input"));
        Assert.IsFalse(next.ExposurePlan.ExactRestore);
        CollectionAssert.AreEquivalent(new[] { "shell", "terminal_input" }, next.ExposurePlan.MissingToolIds!.ToArray());
    }

    [TestMethod]
    public void UnavailableSeed_DoesNotActivateSiblingTools()
    {
        var allowed = Catalog().Where(t => t.Name != "file_patch").ToArray();
        var plan = ToolExposurePlanner.CreatePlan(allowed, Set("file_patch", "unknown_tool"));
        CollectionAssert.AreEqual(new[] { "search_tools" }, Names(plan.VisibleTools));
    }

    private static HashSet<string> Set(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);
    private static string[] Names(IEnumerable<LlmToolDefinition> tools) => tools.Select(t => t.Name).ToArray();
    private static LlmToolDefinition[] Catalog() => new[] { "search_tools" }.Concat(Capabilities)
        .Concat(Enumerable.Range(0, 12).Select(i => $"unrelated_{i}"))
        .Select(name => new LlmToolDefinition { Name = name, Description = name,
            Parameters = new([], []) }).ToArray();
}
