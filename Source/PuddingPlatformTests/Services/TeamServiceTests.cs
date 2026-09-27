using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-12 team slice: the operations sunk out of TeamApiController (which used the DbContext directly).
/// Team deletion is guarded by its workspaces, membership is constrained, and workspace whitelist entries
/// reject None.
/// </summary>
[TestClass]
public sealed class TeamServiceTests
{
    [TestMethod]
    public async Task TeamsAreCreatedUpdatedAndOnlyDeletableWhenEmpty()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateAsync(new TeamDraft(" ", "Name", null, true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.CreateAsync(new TeamDraft("team-x", "  ", null, true))).Status);

        var created = await harness.Service.CreateAsync(new TeamDraft("team-x", "Team X", "notes", true));
        Assert.AreEqual(SkillHubStatus.Ok, created.Status);
        Assert.AreEqual("team-x", created.Value!.TeamId);
        Assert.IsTrue(created.Value.IsEnabled);

        Assert.AreEqual(SkillHubStatus.Conflict,
            (await harness.Service.CreateAsync(new TeamDraft("team-x", "Again", null, true))).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateAsync("ghost",
            new TeamDraft("ghost", "N", null, true))).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteAsync("ghost")).Status);

        var updated = await harness.Service.UpdateAsync("team-x", new TeamDraft("team-x", "Renamed", "d", false));
        Assert.AreEqual(SkillHubStatus.Ok, updated.Status);
        Assert.AreEqual("Renamed", updated.Value!.Name);
        Assert.IsFalse(updated.Value.IsEnabled);

        // 团队下还有工作区时不允许删除。
        await harness.Service.CreateWorkspaceAsync("team-x", new TeamWorkspaceDraft(
            "team-x-space", "Space", null, null, "Manage", "Manage", true));
        var blocked = await harness.Service.DeleteAsync("team-x");
        Assert.AreEqual(SkillHubStatus.BadRequest, blocked.Status);
        Assert.IsNotNull(await harness.Service.GetAsync("team-x"));

        await harness.Service.DeleteWorkspaceAsync("team-x-space");
        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.DeleteAsync("team-x")).Status);
        Assert.IsNull(await harness.Service.GetAsync("team-x"));
    }

    [TestMethod]
    public async Task TeamMembersRequireARealUserAndAValidRole()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new TeamDraft("team-a", "Team A", null, true));

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListMembersAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.AddMemberAsync("ghost", "member-user", "Member")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.AddMemberAsync("team-a", "ghost-user", "Member")).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.AddMemberAsync("team-a", "member-user", "Owner")).Status);

        var added = await harness.Service.AddMemberAsync("team-a", "member-user", "admin");
        Assert.AreEqual(SkillHubStatus.Ok, added.Status);
        Assert.AreEqual("Admin", added.Value!.Role);
        Assert.AreEqual("member-user", added.Value.UserId);

        Assert.AreEqual(SkillHubStatus.Conflict,
            (await harness.Service.AddMemberAsync("team-a", "member-user", "Member")).Status);
        Assert.AreEqual(1, (await harness.Service.ListMembersAsync("team-a")).Value!.Count);

        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.RemoveMemberAsync("team-a", "admin")).Status);
        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.RemoveMemberAsync("team-a", "member-user")).Status);
        Assert.IsEmpty((await harness.Service.ListMembersAsync("team-a")).Value!);
    }

    [TestMethod]
    public async Task TeamWorkspacesValidateIdsPoliciesAndProtectTheDefaultWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new TeamDraft("team-a", "Team A", null, true));

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListWorkspacesAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.CreateWorkspaceAsync("ghost",
            new TeamWorkspaceDraft("space", "S", null, null, "Manage", "Manage", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateWorkspaceAsync("team-a",
            new TeamWorkspaceDraft(" ", "S", null, null, "Manage", "Manage", true))).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.CreateWorkspaceAsync("team-a",
            new TeamWorkspaceDraft("space", "S", null, null, "Owner", "Manage", true))).Status);

        var created = await harness.Service.CreateWorkspaceAsync("team-a", new TeamWorkspaceDraft(
            "team-a-space", "Team A Space", "d", null, "Manage", "ReadOnly", true));
        Assert.AreEqual(SkillHubStatus.Ok, created.Status);
        Assert.AreEqual("team-a", created.Value!.TeamId);
        Assert.AreEqual("ReadOnly", created.Value.CompanyAccessPolicy);
        Assert.AreEqual(SkillHubStatus.Conflict, (await harness.Service.CreateWorkspaceAsync("team-a",
            new TeamWorkspaceDraft("team-a-space", "Again", null, null, "Manage", "Manage", true))).Status);

        // 单查与更新都按 workspace id 作用域。
        Assert.IsNotNull(await harness.Service.FindWorkspaceAsync("team-a-space"));
        Assert.IsNull(await harness.Service.FindWorkspaceAsync("ghost"));
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.UpdateWorkspaceAsync("ghost",
            new TeamWorkspaceDraft("ghost", "N", null, null, "Manage", "Manage", true))).Status);
        var updated = await harness.Service.UpdateWorkspaceAsync("team-a-space", new TeamWorkspaceDraft(
            "team-a-space", "Renamed Space", "d", null, "ReadOnly", "None", false));
        Assert.AreEqual(SkillHubStatus.Ok, updated.Status);
        Assert.AreEqual("ReadOnly", updated.Value!.TeamAccessPolicy);
        Assert.AreEqual("None", updated.Value.CompanyAccessPolicy);
        Assert.IsFalse(updated.Value.IsEnabled);

        // 这一路原先没有默认工作区保护；现在与 WorkspaceApiController 一致。
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.DeleteWorkspaceAsync("default")).Status);
        Assert.IsNotNull(await harness.Service.FindWorkspaceAsync("default"));
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteWorkspaceAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.DeleteWorkspaceAsync("team-a-space")).Status);
        Assert.IsNull(await harness.Service.FindWorkspaceAsync("team-a-space"));
    }

    [TestMethod]
    public async Task WorkspaceWhitelistRejectsNoneAndStaysInsideItsWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new TeamDraft("team-a", "Team A", null, true));
        await harness.Service.CreateWorkspaceAsync("team-a", new TeamWorkspaceDraft(
            "team-a-space", "A", null, null, "Manage", "Manage", true));
        await harness.Service.CreateWorkspaceAsync("team-a", new TeamWorkspaceDraft(
            "team-b-space", "B", null, null, "Manage", "Manage", true));

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListWorkspaceMembersAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.AddWorkspaceMemberAsync("ghost", "member-user", "Write")).Status);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.AddWorkspaceMemberAsync("team-a-space", "ghost-user", "Write")).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.AddWorkspaceMemberAsync("team-a-space", "member-user", "Superuser")).Status);
        // None 不是可用的白名单级别。
        Assert.AreEqual(SkillHubStatus.BadRequest,
            (await harness.Service.AddWorkspaceMemberAsync("team-a-space", "member-user", "None")).Status);

        var added = await harness.Service.AddWorkspaceMemberAsync("team-a-space", "member-user", "Write");
        Assert.AreEqual(SkillHubStatus.Ok, added.Status);
        Assert.AreEqual("Write", added.Value!.AccessLevel);
        Assert.AreEqual(SkillHubStatus.Conflict,
            (await harness.Service.AddWorkspaceMemberAsync("team-a-space", "member-user", "Manage")).Status);

        // 另一个工作区看不到这条白名单，也不能按行 ID 删掉它。
        Assert.IsEmpty((await harness.Service.ListWorkspaceMembersAsync("team-b-space")).Value!);
        Assert.AreEqual(SkillHubStatus.NotFound,
            (await harness.Service.RemoveWorkspaceMemberAsync("team-b-space", added.Value.Id)).Status);
        Assert.AreEqual(1, (await harness.Service.ListWorkspaceMembersAsync("team-a-space")).Value!.Count);

        Assert.AreEqual(SkillHubStatus.Ok,
            (await harness.Service.RemoveWorkspaceMemberAsync("team-a-space", added.Value.Id)).Status);
        Assert.IsEmpty((await harness.Service.ListWorkspaceMembersAsync("team-a-space")).Value!);
    }

    [TestMethod]
    public void VocabulariesMatchCoreEnumsAndUndefinedValuesAreRejected()
    {
        CollectionAssert.AreEqual(new[] { "Member", "Admin" }, TeamService.MemberRoles.ToArray());
        CollectionAssert.AreEqual(new[] { "None", "ReadOnly", "Write", "Manage" }, TeamService.AccessLevels.ToArray());
        Assert.IsTrue(TeamService.TryParseMemberRole("admin", out var role));
        Assert.AreEqual(TeamMemberRole.Admin, role);
        Assert.IsFalse(TeamService.TryParseMemberRole("Owner", out _));
        Assert.IsTrue(TeamService.TryParseAccessLevel("readonly", out var level));
        Assert.AreEqual(WorkspaceAccessPolicy.ReadOnly, level);
        // 未定义的枚举数值也必须被拒绝，否则会写进一个无法解释的权限。
        Assert.IsFalse(TeamService.TryParseAccessLevel("99", out _));
        Assert.IsFalse(TeamService.TryParseMemberRole("99", out _));
    }

    private sealed class Harness(PlatformDbContext db, SqliteConnection connection, TeamService service)
        : IAsyncDisposable
    {
        public TeamService Service => service;

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
            return new Harness(db, connection, new TeamService(db));
        }

        /// <summary>EnsureCreated also applies model seed data, so every insert is idempotent.</summary>
        private static async Task SeedAsync(PlatformDbContext db)
        {
            if (!await db.Teams.AnyAsync(team => team.TeamId == "team-a"))
                db.Teams.Add(new TeamEntity { TeamId = "team-a", Name = "Team A", IsEnabled = true });
            if (!await db.Teams.AnyAsync(team => team.TeamId == "team-b"))
                db.Teams.Add(new TeamEntity { TeamId = "team-b", Name = "Team B", IsEnabled = true });
            await db.SaveChangesAsync();

            if (!await db.Workspaces.AnyAsync(workspace => workspace.WorkspaceId == "default"))
                db.Workspaces.Add(new WorkspaceEntity
                {
                    WorkspaceId = "default",
                    Slug = "default",
                    TeamEntityId = (await db.Teams.FirstAsync(team => team.TeamId == "team-a")).Id,
                    Name = "Default",
                    TeamAccessPolicy = WorkspaceAccessPolicy.Manage,
                    CompanyAccessPolicy = WorkspaceAccessPolicy.Manage,
                });
            if (!await db.AppUsers.AnyAsync(user => user.UserId == "member-user"))
                db.AppUsers.Add(new AppUserEntity
                {
                    UserId = "member-user", Username = "member", DisplayName = "Member",
                    Email = "member@example.invalid",
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
