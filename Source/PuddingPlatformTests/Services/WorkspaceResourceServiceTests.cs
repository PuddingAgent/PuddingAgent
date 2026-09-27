using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-05 resource slice: knowledge bases, workspace skills and workflows, sunk out of three controllers
/// that used the DbContext directly. The invariant that matters is workspace isolation.
/// </summary>
[TestClass]
public sealed class WorkspaceResourceServiceTests
{
    [TestMethod]
    public async Task KnowledgeBasesAreScopedToTheirWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        var missing = await harness.Service.ListKnowledgeBasesAsync("ghost");
        Assert.AreEqual(SkillHubStatus.NotFound, missing.Status);

        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateKnowledgeBaseAsync(
            "team-a-space", new KnowledgeBaseDraft(" ", null, "VectorStore", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateKnowledgeBaseAsync(
            "team-a-space", new KnowledgeBaseDraft("Name", null, " ", true))).Status);

        var created = await harness.Service.CreateKnowledgeBaseAsync("team-a-space",
            new KnowledgeBaseDraft("Docs", "notes", "VectorStore", true));
        Assert.AreEqual(SkillHubStatus.Ok, created.Status);
        Assert.AreEqual(0, created.Value!.DocumentCount);
        Assert.IsTrue(created.Value.IsEnabled);
        var kbId = created.Value.KbId;

        // The other workspace sees nothing and cannot address this id.
        Assert.IsEmpty((await harness.Service.ListKnowledgeBasesAsync("team-b-space")).Value!);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateKnowledgeBaseAsync(
            "team-b-space", kbId, new KnowledgeBaseDraft("Hijacked", null, "Graph", true))).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteKnowledgeBaseAsync("team-b-space", kbId)).Status);
        Assert.AreEqual("Docs", (await harness.Service.ListKnowledgeBasesAsync("team-a-space")).Value!.Single().Name);

        var updated = await harness.Service.UpdateKnowledgeBaseAsync("team-a-space", kbId,
            new KnowledgeBaseDraft("Docs v2", "edited", "Graph", false));
        Assert.AreEqual(SkillHubStatus.Ok, updated.Status);
        Assert.AreEqual("Graph", updated.Value!.KbType);
        Assert.IsFalse(updated.Value.IsEnabled);
        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.DeleteKnowledgeBaseAsync("team-a-space", kbId)).Status);
        Assert.IsEmpty((await harness.Service.ListKnowledgeBasesAsync("team-a-space")).Value!);
    }

    [TestMethod]
    public async Task SkillConfigJsonIsNormalizedByCoresOwnMcpParser()
    {
        await using var harness = await Harness.CreateAsync();
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListSkillsAsync("ghost")).Status);

        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateSkillAsync(
            "team-a-space", new WorkspaceSkillDraft(" ", null, "MCP", "{}", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateSkillAsync(
            "team-a-space", new WorkspaceSkillDraft("Name", null, " ", null, true))).Status);

        // A non-MCP skill keeps its type and config verbatim.
        var builtIn = await harness.Service.CreateSkillAsync("team-a-space",
            new WorkspaceSkillDraft("Builtin", null, "BuiltIn", "{\"raw\":1}", true));
        Assert.AreEqual(SkillHubStatus.Ok, builtIn.Status);
        Assert.AreEqual("BuiltIn", builtIn.Value!.SkillType);
        Assert.AreEqual("{\"raw\":1}", builtIn.Value.ConfigJson);

        // An MCP skill is normalized to the canonical uppercase type, and invalid config is refused.
        var invalid = await harness.Service.CreateSkillAsync("team-a-space",
            new WorkspaceSkillDraft("Broken", null, "mcp", "{not json", true));
        Assert.AreEqual(SkillHubStatus.BadRequest, invalid.Status);
        Assert.AreEqual(1, (await harness.Service.ListSkillsAsync("team-a-space")).Value!.Count);
    }

    [TestMethod]
    public async Task SkillsAndWorkflowsStayInsideTheirWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        var skill = await harness.Service.CreateSkillAsync("team-a-space",
            new WorkspaceSkillDraft("Skill", "d", "CustomScript", null, true));
        var workflow = await harness.Service.CreateWorkflowAsync("team-a-space",
            new WorkspaceWorkflowDraft("Flow", "d", "{\"steps\":[]}", "Draft", true));
        Assert.AreEqual(SkillHubStatus.Ok, skill.Status);
        Assert.AreEqual(SkillHubStatus.Ok, workflow.Status);

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateSkillAsync(
            "team-b-space", skill.Value!.SkillId, new WorkspaceSkillDraft("X", null, "MCP", "{}", true))).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteSkillAsync("team-b-space", skill.Value.SkillId)).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateWorkflowAsync(
            "team-b-space", workflow.Value!.WorkflowId,
            new WorkspaceWorkflowDraft("X", null, "{}", "Active", true))).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteWorkflowAsync("team-b-space", workflow.Value.WorkflowId)).Status);
        Assert.AreEqual(1, (await harness.Service.ListSkillsAsync("team-a-space")).Value!.Count);
        Assert.AreEqual(1, (await harness.Service.ListWorkflowsAsync("team-a-space")).Value!.Count);

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListWorkflowsAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListSkillsAsync("ghost")).Status);
    }

    [TestMethod]
    public async Task WorkflowDefinitionMustBeJsonAndNameIsRequired()
    {
        await using var harness = await Harness.CreateAsync();
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateWorkflowAsync(
            "team-a-space", new WorkspaceWorkflowDraft(" ", null, "{}", "Draft", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateWorkflowAsync(
            "team-a-space", new WorkspaceWorkflowDraft("Flow", null, "{}", " ", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateWorkflowAsync(
            "team-a-space", new WorkspaceWorkflowDraft("Flow", null, "{not json", "Draft", true))).Status);
        // An empty definition is allowed: Core stores it as-is and the page must not invent a schema.
        var empty = await harness.Service.CreateWorkflowAsync("team-a-space",
            new WorkspaceWorkflowDraft("Flow", null, "", "Active", true));
        Assert.AreEqual(SkillHubStatus.Ok, empty.Status);
        Assert.AreEqual("Active", empty.Value!.Status);
        Assert.AreEqual("", empty.Value.DefinitionJson);
    }

    [TestMethod]
    public void ResourceVocabulariesAreTheOnesCoreAccepts()
    {
        CollectionAssert.AreEqual(new[] { "VectorStore", "Graph", "FileIndex" }, WorkspaceResourceService.KnowledgeBaseTypes.ToArray());
        CollectionAssert.AreEqual(new[] { "MCP", "BuiltIn", "CustomScript", "HttpTool" }, WorkspaceResourceService.SkillTypes.ToArray());
        CollectionAssert.AreEqual(new[] { "Draft", "Active", "Paused" }, WorkspaceResourceService.WorkflowStatuses.ToArray());
    }

    private sealed class Harness(PlatformDbContext db, SqliteConnection connection, WorkspaceResourceService service)
        : IAsyncDisposable
    {
        public WorkspaceResourceService Service => service;

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
            // No MCP connection manager: the refresh hook must be optional, not required.
            return new Harness(db, connection, new WorkspaceResourceService(db));
        }

        private static async Task SeedAsync(PlatformDbContext db)
        {
            foreach (var (teamId, name) in new[] { ("team-a", "Team A"), ("team-b", "Team B") })
                if (!await db.Teams.AnyAsync(team => team.TeamId == teamId))
                    db.Teams.Add(new TeamEntity { TeamId = teamId, Name = name });
            await db.SaveChangesAsync();

            foreach (var (workspaceId, teamId) in new[] { ("team-a-space", "team-a"), ("team-b-space", "team-b") })
                if (!await db.Workspaces.AnyAsync(workspace => workspace.WorkspaceId == workspaceId))
                    db.Workspaces.Add(new WorkspaceEntity
                    {
                        WorkspaceId = workspaceId,
                        Slug = workspaceId,
                        TeamEntityId = (await db.Teams.FirstAsync(team => team.TeamId == teamId)).Id,
                        Name = workspaceId,
                        TeamAccessPolicy = WorkspaceAccessPolicy.Manage,
                        CompanyAccessPolicy = WorkspaceAccessPolicy.Manage,
                    });
            await db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
            SqliteConnection.ClearAllPools();
        }
    }
}
