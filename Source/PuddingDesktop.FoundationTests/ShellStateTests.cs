using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

public sealed class ShellStateTests
{
    private static RoleSummary Role(string workspace, string agent, string session = "main")
        => new(new(workspace, agent), "global/code-agent", "同名代码角色", "实现", session, "offline");

    [Fact]
    public void SameTemplateAndName_DraftsAreIsolatedByWorkspaceAgentAndSession()
    {
        var state = new ShellState();
        var a = Role("w1", "a"); var b = Role("w1", "b"); var c = Role("w2", "a");
        state.ReplaceRoles([a, b, c]);
        foreach (var role in new[] { a, b, c })
        {
            state.SelectRole(role.Identity);
            Assert.Equal("", state.Draft);
            Assert.True(state.TrySetDraft(state.SelectionGeneration, role.Identity.ToString()));
        }
        state.SelectRole(a.Identity);
        Assert.Equal(a.Identity.ToString(), state.Draft);
        state.ReplaceRoles([a with { MainSessionId = "new-session" }]);
        Assert.Equal("", state.Draft);
    }

    [Fact]
    public void DelayedDraftFromPreviousRoleIsRejected()
    {
        var state = new ShellState(); var a = Role("w", "a"); var b = Role("w", "b");
        state.ReplaceRoles([a, b]); state.SelectRole(a.Identity);
        var oldGeneration = state.SelectionGeneration;
        state.SelectRole(b.Identity);
        Assert.False(state.TrySetDraft(oldGeneration, "wrong recipient"));
        Assert.Equal("", state.Draft);
    }

    [Fact]
    public void SwitchingRoleDoesNotReassignSelectedDocument()
    {
        var state = new ShellState(); var a = Role("w", "a"); var b = Role("w", "b");
        state.ReplaceRoles([a, b]); state.SelectRole(a.Identity);
        var document = new WorkspaceDocument("d", WorkspaceDocumentKind.Diff, "change", "patch:1", state.ActiveContext!, "diff");
        state.OpenDocument(document); state.SelectRole(b.Identity);
        Assert.Same(document, state.SelectedDocument);
        Assert.Equal(a.Identity, state.SelectedDocument!.Owner.Agent);
        Assert.Throws<InvalidOperationException>(() => state.OpenDocument(document with { Owner = state.ActiveContext! }));
        Assert.Single(state.Documents);
    }

    [Fact]
    public void ClosingActiveDocumentSelectsAdjacent_ClosingOtherKeepsSelection()
    {
        var state = new ShellState(); var owner = new WorkContext(new("w", "a"), "main");
        foreach (var id in new[] { "a", "b", "c" }) state.OpenDocument(new(id, WorkspaceDocumentKind.File, id, id, owner, ""));
        state.CloseDocument("a"); Assert.Equal("c", state.SelectedDocument?.Id);
        state.CloseDocument("c"); Assert.Equal("b", state.SelectedDocument?.Id);
        state.CloseDocument("b"); Assert.Null(state.SelectedDocument);
    }

    [Fact]
    public void DuplicateSnapshotRejectedWithoutLosingCurrentSelection()
    {
        var state = new ShellState(); var role = Role("w", "a");
        state.ReplaceRoles([role]); state.SelectRole(role.Identity);
        Assert.Throws<ArgumentException>(() => state.ReplaceRoles([role, role]));
        Assert.Equal(role, state.SelectedRole);
    }

    [Fact]
    public void RemovingSelectedRoleDisablesDraftButKeepsRecoveryPagesAvailable()
    {
        var state = new ShellState(); var role = Role("w", "a");
        state.ReplaceRoles([role]); state.SelectRole(role.Identity); state.ReplaceRoles([]);
        Assert.Null(state.ActiveContext);
        Assert.False(state.TrySetDraft(state.SelectionGeneration, "orphan"));
        state.Navigate(ShellPage.Settings); Assert.Equal(ShellPage.Settings, state.Page);
        state.Navigate(ShellPage.RuntimeCenter); Assert.Equal(ShellPage.RuntimeCenter, state.Page);
    }

    [Fact]
    public void ReopeningDocumentDoesNotDuplicateTab()
    {
        var state = new ShellState();
        var doc = new WorkspaceDocument("d", WorkspaceDocumentKind.Terminal, "output", "terminal:1", new(new("w", "a"), "s"), "exit 0");
        state.OpenDocument(doc); state.OpenDocument(doc);
        Assert.Single(state.Documents);
    }
}
