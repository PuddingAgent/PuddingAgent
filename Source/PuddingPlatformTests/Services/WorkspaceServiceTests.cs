using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingCode.Skills;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// DS-05 workspace slice: the application operation sunk out of WorkspaceApiController (which used the
/// DbContext directly). Workspace isolation, member permissions and the built-in workspace rule are pinned
/// here without HTTP.
/// </summary>
[TestClass]
public sealed class WorkspaceServiceTests
{
    [TestMethod]
    public async Task CreateValidatesIdentifierTeamAndPolicies()
    {
        await using var harness = await Harness.CreateAsync();
        var valid = new WorkspaceDraft("team-a-space", "team-a", "Team A Space", "notes", "Manage", "ReadOnly", "{}");

        var missingId = await harness.Service.CreateAsync(valid with { WorkspaceId = " " });
        Assert.AreEqual(SkillHubStatus.BadRequest, missingId.Status);
        var missingName = await harness.Service.CreateAsync(valid with { Name = "" });
        Assert.AreEqual(SkillHubStatus.BadRequest, missingName.Status);
        var missingTeam = await harness.Service.CreateAsync(valid with { TeamId = "no-such-team" });
        Assert.AreEqual(SkillHubStatus.BadRequest, missingTeam.Status);
        var badPolicy = await harness.Service.CreateAsync(valid with { TeamAccessPolicy = "Superuser" });
        Assert.AreEqual(SkillHubStatus.BadRequest, badPolicy.Status);

        var created = await harness.Service.CreateAsync(valid);
        Assert.AreEqual(SkillHubStatus.Ok, created.Status);
        Assert.AreEqual("team-a", created.Value!.TeamId);
        Assert.AreEqual("Team A", created.Value.TeamName);
        Assert.IsTrue(created.Value.IsEnabled, "新建工作区默认启用");
        Assert.IsFalse(created.Value.IsFrozen);

        var duplicate = await harness.Service.CreateAsync(valid with { Name = "Again" });
        Assert.AreEqual(SkillHubStatus.Conflict, duplicate.Status);
        // EnsureCreated 种下的 default 工作区也在列表里，所以按名字断言而不是按总数。
        Assert.AreEqual(0, (await harness.Service.ListAsync()).Count(workspace => workspace.Name == "Again"));
    }

    [TestMethod]
    public async Task UpdateAndFreezeKeepWorkspaceIsolation()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new WorkspaceDraft("team-a-space", "team-a", "Team A Space", null, "Manage", "Manage", null));
        await harness.Service.CreateAsync(new WorkspaceDraft("team-b-space", "team-b", "Team B Space", null, "Manage", "Manage", null));

        var unknown = await harness.Service.UpdateAsync("ghost", new WorkspaceEdit("X", null, "Manage", "Manage", true, null));
        Assert.AreEqual(SkillHubStatus.NotFound, unknown.Status);

        var badPolicy = await harness.Service.UpdateAsync("team-a-space",
            new WorkspaceEdit("Renamed", null, "Owner", "Manage", true, null));
        Assert.AreEqual(SkillHubStatus.BadRequest, badPolicy.Status);

        var updated = await harness.Service.UpdateAsync("team-a-space",
            new WorkspaceEdit("Renamed", "desc", "ReadOnly", "None", false, "{\"theme\":\"dark\"}"));
        Assert.AreEqual(SkillHubStatus.Ok, updated.Status);
        Assert.AreEqual("Renamed", updated.Value!.Name);
        Assert.AreEqual("ReadOnly", updated.Value.TeamAccessPolicy);
        Assert.IsFalse(updated.Value.IsEnabled);
        Assert.AreEqual("{\"theme\":\"dark\"}", updated.Value.UserProfile);
        Assert.AreEqual("Team B Space", (await harness.Service.GetAsync("team-b-space"))!.Name, "更新一个工作区不得影响另一个");

        var frozen = await harness.Service.SetFrozenAsync("team-a-space", frozen: true);
        Assert.IsTrue(frozen.Value!.IsFrozen);
        Assert.IsFalse((await harness.Service.GetAsync("team-b-space"))!.IsFrozen);
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.SetFrozenAsync("ghost", true)).Status);
    }

    [TestMethod]
    public async Task BuiltInDefaultWorkspaceCannotBeDeleted()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new WorkspaceDraft("scratch", "team-a", "Scratch", null, "Manage", "Manage", null));

        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.DeleteAsync("default")).Status);
        Assert.IsNotNull(await harness.Service.GetAsync("default"), "默认工作区必须仍然存在");
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.DeleteAsync("ghost")).Status);
        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.DeleteAsync("scratch")).Status);
        Assert.IsNull(await harness.Service.GetAsync("scratch"));
    }

    [TestMethod]
    public async Task MembersRequireARealUserAndStayInsideTheirWorkspace()
    {
        await using var harness = await Harness.CreateAsync();
        await harness.Service.CreateAsync(new WorkspaceDraft("team-a-space", "team-a", "Team A Space", null, "Manage", "Manage", null));
        await harness.Service.CreateAsync(new WorkspaceDraft("team-b-space", "team-b", "Team B Space", null, "Manage", "Manage", null));

        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.ListMembersAsync("ghost")).Status);
        Assert.IsNotNull((await harness.Service.ListMembersAsync("team-a-space")).Value);
        // 工作区不存在时先返回 NotFound，不会去校验用户或权限。
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.AddMemberAsync("ghost", "admin", "Manage")).Status);

        var result = await harness.Service.AddMemberAsync("team-a-space", "member-user", "Write");
        Assert.AreEqual(SkillHubStatus.Ok, result.Status);
        Assert.AreEqual("Write", result.Value!.AccessLevel);
        Assert.AreEqual("member-user", result.Value.UserId);

        Assert.AreEqual(SkillHubStatus.Conflict, (await harness.Service.AddMemberAsync("team-a-space", "member-user", "Manage")).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.AddMemberAsync("team-a-space", "admin", "Superuser")).Status);
        Assert.AreEqual(SkillHubStatus.BadRequest, (await harness.Service.AddMemberAsync("team-a-space", "ghost-user", "Manage")).Status);

        var memberId = result.Value.Id;
        // A member id that belongs to the other workspace must not be removable through this one.
        Assert.AreEqual(SkillHubStatus.NotFound, (await harness.Service.RemoveMemberAsync("team-b-space", memberId)).Status);
        Assert.AreEqual(1, (await harness.Service.ListMembersAsync("team-a-space")).Value!.Count);

        Assert.AreEqual(SkillHubStatus.Ok, (await harness.Service.RemoveMemberAsync("team-a-space", memberId)).Status);
        Assert.IsEmpty((await harness.Service.ListMembersAsync("team-a-space")).Value!);
    }

    [TestMethod]
    public void AccessLevelVocabularyMatchesTheCoreEnum()
    {
        CollectionAssert.AreEqual(
            new[] { "None", "ReadOnly", "Write", "Manage" },
            WorkspaceService.AccessLevels.ToArray());
        Assert.IsTrue(WorkspaceService.TryParsePolicy("readonly", out var policy));
        Assert.AreEqual(WorkspaceAccessPolicy.ReadOnly, policy);
        Assert.IsFalse(WorkspaceService.TryParsePolicy("Owner", out _));
        Assert.IsFalse(WorkspaceService.TryParsePolicy("", out _));
        // 未定义的枚举数值也必须被拒绝，否则会写进一个无法解释的权限。
        Assert.IsFalse(WorkspaceService.TryParsePolicy("99", out _));
    }

    private sealed class Harness(PlatformDbContext db, SqliteConnection connection, WorkspaceService service)
        : IAsyncDisposable
    {
        public WorkspaceService Service => service;

        public static async Task<Harness> CreateAsync()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();
            var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            await SeedAsync(db);
            return new Harness(db, connection, new WorkspaceService(db));
        }

        /// <summary>EnsureCreated also applies model seed data, so every insert here is idempotent.</summary>
        private static async Task SeedAsync(PlatformDbContext db)
        {
            foreach (var (teamId, name) in new[] { ("team-a", "Team A"), ("team-b", "Team B") })
                if (!await db.Teams.AnyAsync(team => team.TeamId == teamId))
                    db.Teams.Add(new TeamEntity { TeamId = teamId, Name = name });
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
