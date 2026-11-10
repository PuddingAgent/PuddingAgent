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

            var repository = new InMemorySessionRepository(paths);
            var branches = new BoundChannelSessions(service, repository);
            var agent = new AgentInstanceManifest { WorkspaceId = "ws", AgentInstanceId = "whale", TemplateId = "test" };
            var turn = await branches.ResolveAsync(agent, source, "room", "message", "actor", ct);
            Assert.NotNull(turn);
            Assert.Equal("developer", (await repository.GetAsync(turn.Session, ct))!.OwnerUserId);
            Assert.Null(await branches.ResolveAsync(agent, source with { ExternalUser = "guest" }, "room", "message2", "other", ct));
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

    [Theory]
    [InlineData("terminal_start")]
    [InlineData("plugin_load")]
    [InlineData("file_write")]
    public async Task UnboundGuestCannotReachEvenAnApprovedOrPreviouslyMountedTool(string tool)
    {
        var original = new CapabilityPolicy { RequiresGrantToolNames = ["terminal_start", "file_write"],
            AllowFileWrite = true, AllowShellExecution = true };
        var restricted = ChannelDevelopmentTrust.ExternalGuest(original);
        Assert.Empty(restricted.AllowedToolNames);
        var firewall = new AgentFirewall();
        var decision = await firewall.EvaluateAsync(new FirewallContext { WorkspaceId = "ws", SessionId = "room",
            AgentInstanceId = "whale", UserId = "guest", RuntimeMode = RuntimeExecutionMode.Yolo, Policy = restricted,
            ToolId = tool }, CancellationToken.None);
        Assert.False(decision.Allowed);
        Assert.Equal(FirewallGate.Capability, decision.DeniedAtGate);
    }

    [Fact]
    public void TrustOnlyEnablesSelectedProcessTools()
    {
        var selected = new CapabilityPolicy { RequiresGrantToolNames = ["terminal_start"] };
        Assert.True(ChannelDevelopmentTrust.Apply(selected, true).AllowShellExecution);
        Assert.False(ChannelDevelopmentTrust.Apply(selected, false).AllowShellExecution);
        Assert.False(ChannelDevelopmentTrust.Apply(new(), true).AllowShellExecution);
        Assert.False(ChannelDevelopmentTrust.Apply(selected, true).AllowFileWrite);
    }

}
