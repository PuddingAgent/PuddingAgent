using PuddingCodeIndex.Contracts;
using PuddingCodeIndex.Services;

namespace PuddingCodeIndexTests.Services.CodeIndex;

/// <summary>
/// M1/D1: the coverage state of a <b>legacy</b> row (one whose <c>ScopeState</c> column is NULL) is projected
/// from its lifecycle status. The old <c>_ =&gt; ScopeState.Covered</c> fallback collapsed
/// <c>Unknown</c>/<c>Registering</c>/<c>Removing</c> into "covered by a parent" — i.e. "owns nothing, serves
/// nothing, needs no indexing" — which made such a scope permanently invisible to the attach loop and left a
/// merely interrupted scope looking like redundant data.
/// </summary>
[TestClass]
public sealed class CodeIndexScopeRegistryTests
{
    private const string WorkspaceId = "ws-m1-legacy-projection";

    [TestMethod]
    public async Task Legacy_Registering_Row_Owns_Its_Path_And_Is_Not_Reported_As_Covered()
    {
        using var fixture = CodeIndexFixture.Create();
        await SeedAsync(fixture, "legacy-root", CodeProjectStatus.Registering);

        var scope = await FindAsync(fixture, "legacy-root");

        Assert.IsNotNull(scope);
        Assert.AreEqual(
            ScopeState.Active,
            scope!.State,
            "a scope that still owes a run still owns its path; reporting it as Covered hid it from the attach loop");
    }

    [TestMethod]
    public async Task Legacy_Unknown_Row_Is_Never_Reported_As_Covered()
    {
        using var fixture = CodeIndexFixture.Create();
        await SeedAsync(fixture, "legacy-unknown", CodeProjectStatus.Unknown);

        var scope = await FindAsync(fixture, "legacy-unknown");

        Assert.IsNotNull(scope);
        Assert.AreNotEqual(
            ScopeState.Covered,
            scope!.State,
            "an unestablished lifecycle status must never masquerade as 'covered by a parent'");
    }

    [TestMethod]
    public async Task Legacy_Removing_Row_Stops_Serving_And_Becomes_A_Cleanup_Target()
    {
        using var fixture = CodeIndexFixture.Create();
        await SeedAsync(fixture, "legacy-removing", CodeProjectStatus.Removing);

        var scope = await FindAsync(fixture, "legacy-removing");

        Assert.IsNotNull(scope);
        Assert.AreEqual(
            ScopeState.Removed,
            scope!.State,
            "mid-delete must stop serving and stay a legitimate cleanup target — not look 'covered'");
    }

    [TestMethod]
    public async Task Legacy_Active_And_Failed_Rows_Keep_Their_State()
    {
        using var fixture = CodeIndexFixture.Create();
        await SeedAsync(fixture, "legacy-active", CodeProjectStatus.Active);
        await SeedAsync(fixture, "legacy-failed", CodeProjectStatus.Failed);

        Assert.AreEqual(ScopeState.Active, (await FindAsync(fixture, "legacy-active"))!.State);
        Assert.AreEqual(ScopeState.Failed, (await FindAsync(fixture, "legacy-failed"))!.State);
    }

    /// <summary>
    /// Control: an explicitly recorded <c>ScopeState</c> always wins — the legacy projection must never
    /// overwrite coverage that was actually declared (otherwise fixing D1 would break real covered children).
    /// </summary>
    [TestMethod]
    public async Task Explicit_Scope_State_Beats_The_Legacy_Projection()
    {
        using var fixture = CodeIndexFixture.Create();
        await SeedAsync(
            fixture,
            "declared-covered-child",
            CodeProjectStatus.Registering,
            explicitState: ScopeState.Covered);

        var scope = await FindAsync(fixture, "declared-covered-child");

        Assert.IsNotNull(scope);
        Assert.AreEqual(
            ScopeState.Covered,
            scope!.State,
            "a declared Covered child stays Covered even though its lifecycle status is Registering");
    }

    private static Task SeedAsync(
        CodeIndexFixture fixture,
        string scopeId,
        CodeProjectStatus status,
        ScopeState? explicitState = null) =>
        fixture.Store.UpsertProjectAsync(new CodeProjectRecord(
            WorkspaceId,
            scopeId,
            Path.Combine(fixture.Root, scopeId),
            status,
            DisplayName: scopeId,
            ScopeState: explicitState));

    private static async Task<CodeIndexScope?> FindAsync(CodeIndexFixture fixture, string scopeId)
    {
        // No scheduler on purpose: listing a registry must not enqueue work.
        var registry = new CodeIndexScopeRegistry(fixture.Store);
        var scopes = await registry.ListScopesAsync(WorkspaceId);
        return scopes.SingleOrDefault(s => s.ScopeId == scopeId);
    }
}
