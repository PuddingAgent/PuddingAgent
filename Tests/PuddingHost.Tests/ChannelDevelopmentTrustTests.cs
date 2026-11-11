using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PuddingAgent.Services;
using PuddingCode.Abstractions;
using PuddingCode.Configuration;
using PuddingCode.Platform;
using PuddingCode.Runtime;
using PuddingController.Services;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services;
using PuddingRuntime.Services;

namespace PuddingHost.Tests;

public sealed class ChannelDevelopmentTrustTests
{
    private sealed class Factory(DbContextOptions<PlatformDbContext> options) : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);
    }

    [Theory]
    [InlineData("slack-instance", true)]
    [InlineData("dingtalk-group", true)]
    [InlineData("feishu-instance", false)]
    public async Task BindingIsScopedToTheSenderAndRecheckedAfterChanges(string channel, bool group)
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var paths = PuddingDataPaths.FromRoot(dir.FullName);
            var factory = new Factory(new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite($"Data Source={Path.Combine(dir.FullName, "test.db")};Pooling=false").Options);
            await using var db = factory.CreateDbContext();
            await db.Database.EnsureCreatedAsync();
            var team = new TeamEntity { TeamId = "team", Name = "test" };
            db.Workspaces.Add(new() { WorkspaceId = "ws", Slug = "ws", Name = "ws", Team = team });
            var account = new AppUserEntity { UserId = "developer", Username = "developer", Email = "user@example.invalid",
                PasswordHash = "unused", UserType = UserType.Admin };
            db.AppUsers.Add(account);
            await db.SaveChangesAsync();
            var service = new ChannelDevelopmentTrust(paths, new EphemeralDataProtectionProvider(), factory);
            var source = new ChannelSender(channel, "app", "org", "member", group);
            var config = new ChannelTrustSettings("verified", [new("app", "org", "member", "developer")]);
            var ct = CancellationToken.None;
            Assert.Null(await service.ResolveAsync("ws", "whale", source, ct));
            await service.SaveAsync("ws", "whale", channel, config, ct);
            Assert.Equal("developer", await service.ResolveAsync("ws", "whale", source, ct));
            foreach (var other in new[] { source with { Organization = "other" }, source with { ExternalUser = "guest" },
                source with { App = "other" }, source with { Channel = "other" } })
                Assert.Null(await service.ResolveAsync("ws", "whale", other, ct));
            Assert.Null(await service.ResolveAsync("ws", "other-agent", source, ct));

            // 单一会话：信任解析不再派生会话，只回答「发送者是谁」。
            var developer = await service.ResolveAsync("ws", "whale", source, ct);
            Assert.Equal("developer", developer);
            Assert.Null(await service.ResolveAsync("ws", "whale", source with { ExternalUser = "guest" }, ct));
            var turn = new DevelopmentTurn("ws", "whale", "main-session", "message", "actor", developer!, source);
            var proof = new Dictionary<string, string> { [ChannelDevelopmentTrust.Receipt] = service.Issue(turn) };
            Assert.Equal(turn, service.Verify(proof, "ws", "whale", turn.Session, "message", "actor"));
            Assert.Null(service.Verify(proof, "ws", "whale", turn.Session, "message2", "actor"));
            Assert.Null(service.Verify(proof, "ws", "whale", turn.Session, "message", "other"));
            proof[ChannelDevelopmentTrust.Receipt] = "verified";
            Assert.Null(service.Verify(proof, "ws", "whale", turn.Session, "message", "actor"));
            await service.SaveAsync("ws", "whale", channel, config with { Mode = "restricted" }, ct);
            Assert.Null(await service.ResolveAsync("ws", "whale", turn.Source, ct));
            await service.SaveAsync("ws", "whale", channel, config, ct);
            account.IsEnabled = false;
            await db.SaveChangesAsync();
            Assert.Null(await service.ResolveAsync("ws", "whale", turn.Source, ct));
        }
        finally { dir.Delete(true); }
    }
}
