using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Controllers.Api;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Data.Entities;

namespace PuddingPlatformTests.Services;

[TestClass]
public sealed class RecoveredAdminControllerTests
{
    [TestMethod]
    public async Task UserReadsReturnStableRoleIdsAndDuplicateEmailIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var user = new AppUserEntity { UserId = "one", Username = "one", Email = "one@test.invalid", PasswordHash = "fixture" };
        var other = new AppUserEntity { UserId = "two", Username = "two", Email = "two@test.invalid", PasswordHash = "fixture" };
        var role = new AppRoleEntity { RoleId = "recovery-viewer", Name = "Viewer" };
        db.AddRange(user, other, role);
        await db.SaveChangesAsync();
        db.AppUserRoles.Add(new AppUserRoleEntity { UserEntityId = user.Id, RoleEntityId = role.Id });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var controller = new AppUserApiController(db);
        var result = await controller.Get("one", default);
        var dto = (AppUserDto)((OkObjectResult)result.Result!).Value!;
        CollectionAssert.AreEqual(new[] { "recovery-viewer" }, dto.RoleIds);
        var conflict = await controller.Update("one", new UpdateUserRequest("changed", "two@test.invalid", null, "SimpleUser", true), default);
        Assert.IsInstanceOfType<ConflictObjectResult>(conflict.Result);
        db.ChangeTracker.Clear();
        Assert.AreEqual("one@test.invalid", (await db.AppUsers.SingleAsync(u => u.UserId == "one")).Email);
    }

    [TestMethod]
    public async Task TeamRouteCannotDeleteDefaultWorkspace()
    {
        await using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite("Data Source=:memory:").Options);
        // Rejection must happen before opening or mutating a database.
        Assert.IsInstanceOfType<BadRequestObjectResult>(await new TeamApiController(db).DeleteWorkspace("default", default));
    }
}
