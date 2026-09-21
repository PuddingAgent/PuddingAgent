using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PuddingPlatform.Data;
using PuddingPlatform.Data.Dtos;
using PuddingPlatform.Services;

namespace PuddingPlatformTests.Services;

/// <summary>
/// SKILL Hub 中央技能库服务单元测试——覆盖设计契约 §9 验收第 3 条列出的 7 项：
/// 发布幂等冲突(409)、版本冲突(409)、语义版本比较、ContentHash 计算、
/// 血缘边生成、retire 语义、安装台账 upsert + InstallCount 重算。
/// </summary>
[TestClass]
public sealed class SkillHubServiceTests
{
    // ── 1. 发布幂等冲突：同 SkillId 第二次发布 → Conflict ──────────────

    [TestMethod]
    public async Task Publish_DuplicateSkillId_ReturnsConflict()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        var first = await service.PublishAsync(PublishRequest("hello-world"), CancellationToken.None);
        Assert.IsTrue(first.IsOk, first.Error);

        var second = await service.PublishAsync(PublishRequest("hello-world"), CancellationToken.None);
        Assert.AreEqual(SkillHubStatus.Conflict, second.Status);
    }

    [TestMethod]
    public async Task Publish_InvalidSkillId_ReturnsBadRequest()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        // 大写 / 空格 / 过短均不符合 ^[a-z0-9][a-z0-9\-]{1,127}$
        var bad = await service.PublishAsync(PublishRequest("Bad_Skill"), CancellationToken.None);
        Assert.AreEqual(SkillHubStatus.BadRequest, bad.Status);
    }

    // ── 2. 版本冲突：同 (SkillId, Version) → Conflict ─────────────────

    [TestMethod]
    public async Task PublishVersion_DuplicateVersion_ReturnsConflict()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        var published = await service.PublishAsync(PublishRequest("ver-conflict"), CancellationToken.None);
        Assert.IsTrue(published.IsOk, published.Error);

        var first = await service.PublishVersionAsync("ver-conflict", VersionRequest("1.1.0"), CancellationToken.None);
        Assert.IsTrue(first.IsOk, first.Error);

        var second = await service.PublishVersionAsync("ver-conflict", VersionRequest("1.1.0"), CancellationToken.None);
        Assert.AreEqual(SkillHubStatus.Conflict, second.Status);
    }

    [TestMethod]
    public async Task PublishVersion_UnknownSkill_ReturnsNotFound()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        var result = await service.PublishVersionAsync("missing-skill", VersionRequest("1.1.0"), CancellationToken.None);
        Assert.AreEqual(SkillHubStatus.NotFound, result.Status);
    }

    // ── 3. 语义版本比较：1.10.0 > 1.9.0，多段数字，不抛异常 ─────────────

    [TestMethod]
    public void CompareVersions_MultiSegmentNumeric_OrderingCorrect()
    {
        Assert.IsTrue(SkillHubService.CompareVersions("1.10.0", "1.9.0") > 0, "1.10.0 应大于 1.9.0");
        Assert.IsTrue(SkillHubService.CompareVersions("1.9.0", "1.10.0") < 0);
        Assert.AreEqual(0, SkillHubService.CompareVersions("1.10.0", "1.10.0"));
        Assert.IsTrue(SkillHubService.CompareVersions("2.0.0", "1.99.99") > 0);
        Assert.IsTrue(SkillHubService.CompareVersions("1.0.1", "1.0.0") > 0);

        // 缺失段按 0 处理
        Assert.IsTrue(SkillHubService.CompareVersions("1.2", "1.2.0") == 0, "1.2 与 1.2.0 应相等");

        // 不抛异常：乱输入
        Assert.AreEqual(0, SkillHubService.CompareVersions(null, null));
        Assert.AreEqual(0, SkillHubService.CompareVersions("", " "));
        Assert.IsTrue(SkillHubService.CompareVersions("abc", "1.0.0") < 0, "不可解析段按 0，应小于 1.0.0");
    }

    // ── 4. ContentHash 计算：SHA256 前 16 字节小写 hex ─────────────────

    [TestMethod]
    public void ComputeContentHash_ShouldBeFirst16BytesOfSha256LowercaseHex()
    {
        var markdown = "# Hello Skill\n\nbody";
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(markdown))[..16]).ToLowerInvariant();

        var hash = SkillHubService.ComputeContentHash(markdown);
        Assert.AreEqual(expected, hash);
        Assert.AreEqual(32, hash.Length, "16 字节 = 32 个 hex 字符");
        Assert.IsTrue(hash.All(char.IsAsciiLetterOrDigit), "应为 hex 字符");

        // 空串/空引用不抛异常
        Assert.AreEqual(32, SkillHubService.ComputeContentHash(string.Empty).Length);
        Assert.AreEqual(32, SkillHubService.ComputeContentHash(null!).Length);

        // 同一内容同一 hash；内容变化 hash 变化
        Assert.AreEqual(hash, SkillHubService.ComputeContentHash(markdown));
        Assert.AreNotEqual(hash, SkillHubService.ComputeContentHash(markdown + "x"));
    }

    [TestMethod]
    public async Task Publish_ServerComputesContentHash_IgnoringRequest()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        var published = await service.PublishAsync(PublishRequest("hash-check"), CancellationToken.None);
        Assert.IsTrue(published.IsOk, published.Error);
        Assert.AreEqual(
            SkillHubService.ComputeContentHash(PublishRequest("hash-check").SkillMarkdown),
            published.Value!.LatestContentHash);
    }

    // ── 5. 血缘边生成：ParentVersion 非空 → 边 parent→child ────────────

    [TestMethod]
    public async Task Lineage_EdgesGenerated_FromParentVersion()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        var root = await service.PublishAsync(PublishRequest("evo-skill"), CancellationToken.None);
        Assert.IsTrue(root.IsOk, root.Error);
        var rootVersions = await service.ListVersionsAsync("evo-skill", CancellationToken.None);
        Assert.AreEqual(1, rootVersions!.Count);
        Assert.AreEqual("create", rootVersions[0].EvolutionAction, "首个版本缺省动作应为 create");

        var child = await service.PublishVersionAsync(
            "evo-skill", VersionRequest("1.1.0", parentVersion: "1.0.0", action: "patch"), CancellationToken.None);
        Assert.IsTrue(child.IsOk, child.Error);
        Assert.AreEqual("patch", child.Value!.EvolutionAction);

        var grandchild = await service.PublishVersionAsync(
            "evo-skill", VersionRequest("2.0.0", parentVersion: "1.1.0", action: "fork"), CancellationToken.None);
        Assert.IsTrue(grandchild.IsOk, grandchild.Error);

        var lineage = await service.GetSkillLineageAsync("evo-skill", CancellationToken.None);
        Assert.IsNotNull(lineage);
        Assert.AreEqual(3, lineage.Nodes.Count, "3 个版本 → 3 个节点");
        var root0 = lineage.Nodes.Single(n => n.NodeId == "evo-skill@1.0.0");
        Assert.IsNull(root0.ParentNodeId, "根节点 ParentNodeId 应为 null");

        Assert.AreEqual(2, lineage.Edges.Count, "2 个子版本 → 2 条边");
        Assert.IsTrue(lineage.Edges.Any(e =>
            e.FromNodeId == "evo-skill@1.0.0" && e.ToNodeId == "evo-skill@1.1.0" && e.Action == "patch"));
        Assert.IsTrue(lineage.Edges.Any(e =>
            e.FromNodeId == "evo-skill@1.1.0" && e.ToNodeId == "evo-skill@2.0.0" && e.Action == "fork"));
    }

    [TestMethod]
    public async Task PublishVersion_DefaultAction_DerivedFromParentVersion()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("default-action"), CancellationToken.None);

        // 带父版本但不给动作 → patch
        var withParent = await service.PublishVersionAsync(
            "default-action", VersionRequest("1.1.0", parentVersion: "1.0.0"), CancellationToken.None);
        Assert.IsTrue(withParent.IsOk, withParent.Error);
        Assert.AreEqual("patch", withParent.Value!.EvolutionAction);

        // 不带父版本也不给动作 → create（新技能场景）— 在 Publish 已验证，这里验证白名单拒绝
        var invalid = await service.PublishVersionAsync(
            "default-action", VersionRequest("1.2.0", parentVersion: "1.1.0", action: "teleport"), CancellationToken.None);
        Assert.AreEqual(SkillHubStatus.BadRequest, invalid.Status, "白名单外动作应 400");
    }

    // ── 6. retire 语义：action=retire → 主档 Status=retired ────────────

    [TestMethod]
    public async Task PublishVersion_RetireAction_SetsMasterStatusRetired()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("retire-me"), CancellationToken.None);
        var retired = await service.PublishVersionAsync(
            "retire-me", VersionRequest("9.9.9", parentVersion: "1.0.0", action: "retire"), CancellationToken.None);
        Assert.IsTrue(retired.IsOk, retired.Error);
        Assert.AreEqual("retire", retired.Value!.EvolutionAction);

        var detail = await service.GetSkillAsync("retire-me", CancellationToken.None);
        Assert.IsNotNull(detail);
        Assert.AreEqual("retired", detail.Skill.Status, "主档 Status 应同步置为 retired");
        Assert.AreEqual("9.9.9", detail.Skill.LatestVersion);
    }

    [TestMethod]
    public async Task Retire_SoftDelete_SetsStatusRetired_KeepsVersions_AndWritesEvent()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("soft-del"), CancellationToken.None);
        var result = await service.RetireAsync("soft-del", CancellationToken.None);
        Assert.IsTrue(result.IsOk, result.Error);
        Assert.AreEqual("retired", result.Value!.Status);

        // 软删不物理删除版本内容
        var content = await service.GetVersionAsync("soft-del", "1.0.0", CancellationToken.None);
        Assert.IsNotNull(content);
        Assert.IsFalse(string.IsNullOrEmpty(content.SkillMarkdown));

        // 软删写审计事件
        var events = await service.ListEventsAsync("soft-del", 50, CancellationToken.None);
        Assert.IsTrue(events.Any(e => e.EventType == "delete"), "应写 delete 事件");
    }

    // ── 7. 安装台账 upsert + InstallCount 去重重算 ─────────────────────

    [TestMethod]
    public async Task RegisterInstall_Upsert_RecalculatesDistinctAgentInstallCount()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("install-me"), CancellationToken.None);

        // Agent A 安装 1.0.0
        var a1 = await service.RegisterInstallAsync(InstallRequest("install-me", "agent-A", "1.0.0"), CancellationToken.None);
        Assert.IsTrue(a1.IsOk, a1.Error);
        var detail1 = await service.GetSkillAsync("install-me", CancellationToken.None);
        Assert.AreEqual(1, detail1!.Skill.InstallCount);

        // Agent B 安装
        await service.RegisterInstallAsync(InstallRequest("install-me", "agent-B", "1.0.0"), CancellationToken.None);
        var detail2 = await service.GetSkillAsync("install-me", CancellationToken.None);
        Assert.AreEqual(2, detail2!.Skill.InstallCount);

        // Agent A 重复安装（升级到 1.1.0）→ upsert，不去重增加
        var a2 = await service.RegisterInstallAsync(InstallRequest("install-me", "agent-A", "1.1.0"), CancellationToken.None);
        Assert.IsTrue(a2.IsOk, a2.Error);
        var detail3 = await service.GetSkillAsync("install-me", CancellationToken.None);
        Assert.AreEqual(2, detail3!.Skill.InstallCount, "同一 AgentInstanceId 重复登记不增加 InstallCount");
        Assert.AreEqual(1, detail3.RecentInstalls.Count(i => i.AgentInstanceId == "agent-A"),
            "agent-A 只应有一条台账记录");

        // 台账中 agent-A 版本已更新
        var installs = await service.ListInstallsAsync("agent-A", "install-me", 1, 50, CancellationToken.None);
        Assert.AreEqual(1, installs.Count);
        Assert.AreEqual("1.1.0", installs[0].InstalledVersion);
    }

    // ── 待更新清单（updates 端点语义，配套 §9.3 安装台账场景）──────────

    [TestMethod]
    public async Task ListUpdates_ReturnsOnlySkillsWhereInstalledVersionIsOlder()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("upd-skill"), CancellationToken.None);
        await service.PublishVersionAsync("upd-skill", VersionRequest("1.1.0", parentVersion: "1.0.0"), CancellationToken.None);
        await service.PublishVersionAsync("upd-skill", VersionRequest("1.10.0", parentVersion: "1.1.0"), CancellationToken.None);

        // agent 仍停在 1.9.0 → 语义比较应判定落后（1.9.0 < 1.10.0）
        await service.RegisterInstallAsync(InstallRequest("upd-skill", "agent-U", "1.9.0"), CancellationToken.None);
        var updates = await service.ListUpdatesAsync("agent-U", CancellationToken.None);
        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual("1.10.0", updates[0].LatestVersion);
        Assert.AreEqual("1.9.0", updates[0].InstalledVersion);

        // 升到最新 → 清单为空
        await service.RegisterInstallAsync(InstallRequest("upd-skill", "agent-U", "1.10.0"), CancellationToken.None);
        var none = await service.ListUpdatesAsync("agent-U", CancellationToken.None);
        Assert.AreEqual(0, none.Count);
    }

    // ── 审计事件：全部写操作写事件（契约 §5.3.11）─────────────────────

    [TestMethod]
    public async Task AllWriteOperations_WriteAuditEvents()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("audit-skill", agentId: "agent-X"), CancellationToken.None);
        await service.PublishVersionAsync(
            "audit-skill", VersionRequest("1.1.0", parentVersion: "1.0.0", agentId: "agent-X"), CancellationToken.None);
        await service.RegisterInstallAsync(InstallRequest("audit-skill", "agent-Y", "1.1.0"), CancellationToken.None);
        await service.UpdateMetaAsync("audit-skill", new UpdateHubSkillMetaRequest(
            Name: null, Summary: "新摘要", Description: null, Tags: null, Keywords: null,
            Status: "deprecated", Visibility: null), CancellationToken.None);

        var events = await service.ListEventsAsync("audit-skill", 100, CancellationToken.None);
        CollectionAssert.AreEquivalent(
            new[] { "publish", "update_version", "install", "status_change" },
            events.Select(e => e.EventType).Distinct().ToList(),
            "publish/update_version/install/status_change 四类事件都应落审计流（PATCH 同时改了 Status → status_change）");
    }

    // ── 统计 ────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Stats_AggregatesCounters()
    {
        await using var scope = await CreateScopeAsync();
        var service = scope.CreateService();

        await service.PublishAsync(PublishRequest("stat-a"), CancellationToken.None);
        await service.PublishVersionAsync("stat-a", VersionRequest("1.1.0", parentVersion: "1.0.0"), CancellationToken.None);
        await service.PublishAsync(PublishRequest("stat-b"), CancellationToken.None);
        await service.RegisterInstallAsync(InstallRequest("stat-a", "agent-1", "1.1.0"), CancellationToken.None);
        await service.RegisterInstallAsync(InstallRequest("stat-a", "agent-2", "1.1.0"), CancellationToken.None);

        var stats = await service.GetStatsAsync(CancellationToken.None);
        Assert.AreEqual(2, stats.TotalSkills);
        Assert.AreEqual(2, stats.ActiveSkills);
        Assert.AreEqual(3, stats.TotalVersions);
        Assert.AreEqual(2, stats.TotalInstalls);
        Assert.AreEqual(2, stats.DistinctAgents);
        Assert.AreEqual(1, stats.EvolvedSkills, "仅 stat-a 有 >1 个版本");
        Assert.IsTrue(stats.EvolutionActionCounts.Any(c => c.Action == "create" && c.Count == 2));
        Assert.AreEqual(1, stats.TopInstalled.Count);
        Assert.AreEqual("stat-a", stats.TopInstalled[0].SkillId);
    }

    // ── SchemaBootstrapper：旧库幂等补建表（dotnet ef 不可用时的 fallback 验证）──

    [TestMethod]
    public async Task SkillHubSchemaBootstrapper_CreatesTables_Idempotently()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new PlatformDbContext(options);

        // 不调 EnsureCreated，模拟既有旧库缺表场景 → 由 bootstrapper 自愈建表
        await SkillHubSchemaBootstrapper.EnsureCreatedAsync(db);

        db.HubSkills.Add(new PuddingPlatform.Data.Entities.HubSkillEntity { SkillId = "boot-test", Name = "boot" });
        await db.SaveChangesAsync();
        Assert.AreEqual(1, await db.HubSkills.CountAsync(s => s.SkillId == "boot-test"),
            "bootstrapper 建表后应可正常读写 HubSkills");

        // 幂等：重复执行不抛异常、不重复建表
        await SkillHubSchemaBootstrapper.EnsureCreatedAsync(db);
        Assert.AreEqual(1, await db.HubSkills.CountAsync(s => s.SkillId == "boot-test"));
    }

    // ── 测试夹具：SQLite in-memory + EnsureCreated ────────────────────

    private static PublishHubSkillRequest PublishRequest(
        string skillId, string? agentId = null) => new(
        SkillId: skillId,
        Name: $"技能 {skillId}",
        Summary: $"{skillId} 的摘要",
        Description: $"{skillId} 的描述",
        Tags: new[] { "test", skillId },
        Keywords: new[] { skillId },
        Version: "1.0.0",
        SkillMarkdown: $"---\nname: {skillId}\n---\n\n# {skillId}\n\n正文内容",
        ManifestJson: null,
        EvolutionAction: null,
        ParentVersion: null,
        RelatedSkillIds: null,
        PublishedByAgentId: agentId,
        PublishedByWorkspaceId: null,
        PublishNote: null,
        EvidenceJson: null,
        Visibility: "global");

    private static PublishHubSkillRequest VersionRequest(
        string version, string? parentVersion = null, string? action = null, string? agentId = null) => new(
        SkillId: "irrelevant-route-wins",
        Name: "irrelevant",
        Summary: null,
        Description: null,
        Tags: null,
        Keywords: null,
        Version: version,
        SkillMarkdown: $"# version {version}\n\n新内容 {Guid.NewGuid():N}",
        ManifestJson: null,
        EvolutionAction: action,
        ParentVersion: parentVersion,
        RelatedSkillIds: null,
        PublishedByAgentId: agentId,
        PublishedByWorkspaceId: null,
        PublishNote: null,
        EvidenceJson: null,
        Visibility: "global");

    private static RegisterInstallRequest InstallRequest(
        string skillId, string agentInstanceId, string version) => new(
        SkillId: skillId,
        AgentInstanceId: agentInstanceId,
        WorkspaceId: null,
        InstalledVersion: version,
        ContentHash: null,
        InstalledBy: "agent");

    private static async Task<TestScope> CreateScopeAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new PlatformDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return new TestScope(connection, db);
    }

    private sealed class TestScope(SqliteConnection connection, PlatformDbContext db) : IAsyncDisposable
    {
        public SkillHubService CreateService() => new(db);

        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
