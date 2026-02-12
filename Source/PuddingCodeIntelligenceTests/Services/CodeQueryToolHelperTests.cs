using PuddingCodeIntelligence.Contracts;
using PuddingRuntime.Services.Tools;

namespace PuddingCodeIntelligenceTests.Services;

[TestClass]
public sealed class CodeQueryToolHelperTests
{
    [TestMethod]
    public async Task ResolveAndEnsureProjectIdAsync_AllHintsNull_ReturnsNull()
    {
        // Gap 1 fix: when no hints, don't fall back to process CWD.
        // Returns null so code_symbol_search searches all projects,
        // and other tools properly report "project_id required".
        var result = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            resolver: new StubResolver(),
            workspaceId: "ws-1",
            projectId: null,
            filePath: null,
            scopePath: null,
            ct: CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ResolveAndEnsureProjectIdAsync_ResolverNull_ReturnsNull()
    {
        var result = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            resolver: null,
            workspaceId: "ws-1",
            projectId: null,
            filePath: null,
            scopePath: null,
            ct: CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task ResolveAndEnsureProjectIdAsync_ProjectIdProvided_ReturnsTrimmed()
    {
        var result = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            resolver: null,
            workspaceId: "ws-1",
            projectId: "  my-project  ",
            filePath: null,
            scopePath: null,
            ct: CancellationToken.None);

        Assert.AreEqual("my-project", result);
    }

    [TestMethod]
    public async Task ResolveAndEnsureProjectIdAsync_FilePathProvided_AutoDetects()
    {
        var resolver = new StubResolver();

        var result = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            resolver,
            workspaceId: "ws-1",
            projectId: null,
            filePath: "/src/App.cs",
            scopePath: null,
            ct: CancellationToken.None);

        Assert.AreEqual("auto-detected", result);
    }

    [TestMethod]
    public async Task ResolveAndEnsureProjectIdAsync_ScopePathProvided_AutoDetects()
    {
        var resolver = new StubResolver();

        var result = await CodeQueryToolHelper.ResolveAndEnsureProjectIdAsync(
            resolver,
            workspaceId: "ws-1",
            projectId: null,
            filePath: null,
            scopePath: "/src",
            ct: CancellationToken.None);

        Assert.AreEqual("auto-detected", result);
    }

    private sealed class StubResolver : ICodeIndexScopeResolver
    {
        public Task<ScopeResolution?> ResolveAsync(
            string workspaceId,
            string? filePath = null,
            string? scopePath = null,
            string? scopeId = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ScopeResolution?>(null);
        }

        public Task<ScopeResolution> ResolveAndEnsureAsync(
            string workspaceId,
            string? filePath = null,
            string? scopePath = null,
            string? scopeId = null,
            CancellationToken cancellationToken = default)
        {
            var scope = new CodeIndexScope(
                workspaceId,
                "auto-detected",
                "/auto-detected-root",
                ScopeState.Active,
                ScopeSource.Auto,
                "Auto Project");

            return Task.FromResult(new ScopeResolution(scope, true, true));
        }
    }
}
