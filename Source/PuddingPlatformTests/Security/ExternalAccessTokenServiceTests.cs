using PuddingCode.Security;
using PuddingPlatform.Data.Entities;
using PuddingPlatform.Services.Security;

namespace PuddingPlatformTests.Security;

/// <summary>
/// ADR-075 §15.1 Token 与认证矩阵：明文只出现一次、摘要存储、
/// malformed/unknown/wrong/expired/revoked/owner-disabled 全部失败、CAS 管理命令。
/// </summary>
[TestClass]
public sealed class ExternalAccessTokenServiceTests
{
    private const string Owner = "admin";

    [TestMethod]
    public async Task Create_ReturnsCanonicalToken_PlaintextNeverPersisted()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var result = await service.CreateAsync(Command(name: "codex-readonly"));

        Assert.IsTrue(result.IsOk, $"create failed: {result.Error}");
        var canonical = result.Value!.AccessToken;
        Assert.IsTrue(canonical.StartsWith("pdt_v1_"), $"unexpected prefix: {canonical[..16]}…");
        Assert.AreEqual(1, canonical.Count(c => c == '.'));

        // keyId/secret 均为 Base64Url（无 +/=）。
        var parts = canonical["pdt_v1_".Length..].Split('.');
        CollectionAssert.DoesNotContain(parts[0].ToCharArray(), '+');
        Assert.AreEqual(22, parts[0].Length, "keyId = Base64Url(16B)");
        Assert.AreEqual(43, parts[1].Length, "secret = Base64Url(32B)");

        // 持久层只有 32 字节摘要，没有 canonical 明文。
        var hashes = await harness.DumpRawSecretHashesAsync();
        Assert.AreEqual(1, hashes.Count);
        Assert.AreEqual(64, hashes[0].Length, "SHA-256 hex");
        Assert.IsFalse(hashes[0].Contains(canonical, StringComparison.Ordinal));

        // 再查询任何管理端都不返回明文（ListItem 无 Secret 字段为编译期保证）。
        var detail = await service.GetDetailAsync(result.Value.Item.TokenId);
        Assert.IsNotNull(detail);
        Assert.AreEqual(1, detail!.Version);
        Assert.AreEqual(ExternalAccessTokenStatus.Active, detail.Status);
    }

    [TestMethod]
    public async Task Create_SavesScopesAndWorkspaceAllowList()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var result = await service.CreateAsync(Command(
            name: "rw",
            scopes: [ExternalTaskApiScopes.TasksRead, ExternalTaskApiScopes.TasksWrite],
            workspaces: ["default"]));

        Assert.IsTrue(result.IsOk);
        Assert.AreEqual(2, result.Value!.Item.Scopes.Count);
        CollectionAssert.AreEquivalent(
            new[] { "tasks.read", "tasks.write" }, result.Value.Item.Scopes.ToList());
        CollectionAssert.AreEqual(new[] { "default" }, result.Value.Item.Workspaces.ToList());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("over-100-chars-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Create_RejectsInvalidInputs(string name)
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var result = await service.CreateAsync(Command(name: name));
        Assert.IsFalse(result.IsOk);
        Assert.AreEqual(ExternalAccessTokenCreateError.InvalidName, result.Error);
    }

    [TestMethod]
    public async Task Create_RejectsUnknownScope()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var result = await service.CreateAsync(Command(name: "x", scopes: ["tasks.read", "tasks.delete"]));
        Assert.IsFalse(result.IsOk);
        Assert.AreEqual(ExternalAccessTokenCreateError.UnknownScope, result.Error);
    }

    [TestMethod]
    public async Task Create_RejectsMissingOrUnknownWorkspace()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var empty = await service.CreateAsync(Command(name: "x", workspaces: []));
        Assert.AreEqual(ExternalAccessTokenCreateError.NoWorkspaces, empty.Error);

        var unknown = await service.CreateAsync(Command(name: "x", workspaces: ["no-such-workspace"]));
        Assert.AreEqual(ExternalAccessTokenCreateError.UnknownWorkspace, unknown.Error);
    }

    [TestMethod]
    public async Task Create_LifetimeBounds_Default90_Max365()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var defaults = await service.CreateAsync(Command(name: "d"));
        Assert.IsTrue(defaults.IsOk);
        var lifetime = defaults.Value!.Item.ExpiresAtUtc - defaults.Value.Item.CreatedAtUtc;
        Assert.AreEqual(90, Math.Round(lifetime.TotalDays), "默认有效期 90 天");

        var max = await service.CreateAsync(Command(name: "m", lifetimeDays: 365));
        Assert.IsTrue(max.IsOk);

        var over = await service.CreateAsync(Command(name: "o", lifetimeDays: 366));
        Assert.AreEqual(ExternalAccessTokenCreateError.LifetimeOutOfRange, over.Error);

        var under = await service.CreateAsync(Command(name: "u", lifetimeDays: 0));
        Assert.AreEqual(ExternalAccessTokenCreateError.LifetimeOutOfRange, under.Error);
    }

    [TestMethod]
    public async Task Create_EnforcesMaxActiveTokensPerOwner()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), $"pdt-test-{Guid.NewGuid():N}");
        try
        {
            await using var harness = await ExternalAccessTokenTestHarness.CreateAsync(
                new PuddingCode.Configuration.PuddingExternalTaskApiConfig
                {
                    MaxActiveTokensPerOwner = 2,
                    DefaultTokenLifetimeDays = 90,
                    MaxTokenLifetimeDays = 365,
                },
                dataRoot);
            await harness.SeedOwnerAsync(Owner);
            var service = harness.CreateService(dataRoot);

            Assert.IsTrue((await service.CreateAsync(Command(name: "t1"))).IsOk);
            Assert.IsTrue((await service.CreateAsync(Command(name: "t2"))).IsOk);

            var third = await service.CreateAsync(Command(name: "t3"));
            Assert.AreEqual(ExternalAccessTokenCreateError.TooManyActiveTokens, third.Error);

            // 撤销后腾出名额。
            var t1 = (await service.ListAsync(new ExternalAccessTokenListFilter())).Items.First(i => i.Name == "t1");
            Assert.IsTrue((await service.RevokeAsync(t1.TokenId, t1.Version, Owner, "rotate")).IsOk);
            Assert.IsTrue((await service.CreateAsync(Command(name: "t3"))).IsOk);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
                Directory.Delete(dataRoot, recursive: true);
        }
    }

    [TestMethod]
    public async Task Validate_Succeeds_And_FailsOnWrongSecret()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "v"));
        var canonical = created.Value!.AccessToken;

        var ok = await service.ValidateAsync(canonical);
        Assert.IsNotNull(ok.Principal);
        Assert.AreEqual(created.Value.Item.TokenId, ok.Principal.TokenId);
        Assert.AreEqual(Owner, ok.Principal.OwnerUserId);
        CollectionAssert.Contains(ok.Principal.Scopes.ToList(), "tasks.read");
        CollectionAssert.Contains(ok.Principal.Workspaces.ToList(), "default");

        var tampered = canonical[..^2] + (canonical[^1] == 'A' ? "B" : "A");
        var bad = await service.ValidateAsync(tampered);
        Assert.IsNull(bad.Principal);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.BadSecret, bad.FailureReason);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("garbage")]
    [DataRow("pdt_v1_onlykey.")]
    [DataRow("pdt_v1_.onlysecret")]
    [DataRow("pdt_v1_key+bad.secret/value")]
    [DataRow("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.sig")]
    public async Task Validate_MalformedTokens_Rejected(string? presented)
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var service = harness.CreateService();

        var outcome = await service.ValidateAsync(presented);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.Malformed, outcome.FailureReason);
    }

    [TestMethod]
    public async Task Validate_OverLengthToken_RejectedBeforeParse()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var service = harness.CreateService();

        // 超过 256 硬上限：解析前拒绝。
        var overLength = "pdt_v1_" + new string('a', 300) + "." + new string('b', 40);
        var outcome = await service.ValidateAsync(overLength);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.Malformed, outcome.FailureReason);
    }

    [TestMethod]
    public async Task Validate_UnknownKeyId_Rejected()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        var service = harness.CreateService();

        var outcome = await service.ValidateAsync("pdt_v1_QUJDREVGR0hJSktMTU5PUA.QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVphYmNkZWZnaGlqa2xtbm9w");
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.UnknownKey, outcome.FailureReason);
    }

    [TestMethod]
    public async Task Revoke_IsImmediateAndIrreversible_WithCas()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "r"));
        var tokenId = created.Value!.Item.TokenId;

        // 错误 expectedVersion → 冲突。
        var conflict = await service.RevokeAsync(tokenId, expectedVersion: 99, Owner, "x");
        Assert.AreEqual(ExternalAccessTokenManagementError.VersionConflict, conflict.Error);

        // 正确 CAS → 撤销后立即失效。
        Assert.IsTrue((await service.RevokeAsync(tokenId, expectedVersion: 1, Owner, "leaked")).IsOk);
        var revoked = await service.ValidateAsync(created.Value.AccessToken);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.Revoked, revoked.FailureReason);

        // 二次撤销 → 冲突；状态为 Revoked。
        var again = await service.RevokeAsync(tokenId, expectedVersion: 2, Owner, "again");
        Assert.AreEqual(ExternalAccessTokenManagementError.VersionConflict, again.Error);
        var detail = await service.GetDetailAsync(tokenId);
        Assert.AreEqual(ExternalAccessTokenStatus.Revoked, detail!.Status);
        Assert.AreEqual("leaked", detail.RevocationReason);
    }

    [TestMethod]
    public async Task Rename_Cas_OnlyChangesName()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "old-name"));
        var tokenId = created.Value!.Item.TokenId;

        var conflict = await service.RenameAsync(tokenId, 99, "new-name", Owner);
        Assert.AreEqual(ExternalAccessTokenManagementError.VersionConflict, conflict.Error);

        Assert.IsTrue((await service.RenameAsync(tokenId, 1, "new-name", Owner)).IsOk);
        var detail = await service.GetDetailAsync(tokenId);
        Assert.AreEqual("new-name", detail!.Name);
        Assert.AreEqual(2, detail.Version);

        // 重命名不改变安全事实：token 仍可认证，scope/workspace 不变。
        var ok = await service.ValidateAsync(created.Value.AccessToken);
        Assert.IsNotNull(ok.Principal);
    }

    [TestMethod]
    public async Task ExpiredToken_FailsClosed()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "e", lifetimeDays: 30));

        await harness.BackdateExpiryAsync(created.Value!.Item.TokenId, DateTimeOffset.UtcNow.AddMinutes(-1));

        var outcome = await service.ValidateAsync(created.Value.AccessToken);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.Expired, outcome.FailureReason);
    }

    [TestMethod]
    public async Task OwnerDisabled_FailsClosed()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "o"));

        await harness.SetOwnerEnabledAsync(Owner, enabled: false);

        var outcome = await service.ValidateAsync(created.Value!.AccessToken);
        Assert.AreEqual(ExternalAccessTokenAuthFailureReason.OwnerDisabled, outcome.FailureReason);
    }

    [TestMethod]
    public async Task List_FiltersByStatusAndWorkspace_SortsByCreatedDesc()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();

        var a = await service.CreateAsync(Command(name: "a"));
        var b = await service.CreateAsync(Command(name: "b"));
        Assert.IsTrue((await service.RevokeAsync(b.Value!.Item.TokenId, 1, Owner, "done")).IsOk);

        var all = await service.ListAsync(new ExternalAccessTokenListFilter());
        Assert.AreEqual(2, all.Total);
        Assert.AreEqual("b", all.Items[0].Name, "按创建时间倒序（最新在前）");

        var activeOnly = await service.ListAsync(new ExternalAccessTokenListFilter
        {
            Status = ExternalAccessTokenStatus.Active,
        });
        Assert.AreEqual(1, activeOnly.Total);
        Assert.AreEqual(a.Value!.Item.TokenId, activeOnly.Items[0].TokenId);

        var revokedOnly = await service.ListAsync(new ExternalAccessTokenListFilter
        {
            Status = ExternalAccessTokenStatus.Revoked,
        });
        Assert.AreEqual(b.Value!.Item.TokenId, revokedOnly.Items[0].TokenId);
    }

    [TestMethod]
    public async Task AuditEvents_RecordedForManagementOperations()
    {
        await using var harness = await ExternalAccessTokenTestHarness.CreateAsync();
        await harness.SeedOwnerAsync(Owner);
        var service = harness.CreateService();
        var created = await service.CreateAsync(Command(name: "audit"));
        await service.RenameAsync(created.Value!.Item.TokenId, 1, "audit-2", Owner);
        await service.RevokeAsync(created.Value!.Item.TokenId, 2, Owner, "test");

        await using var db = await harness.Factory.CreateDbContextAsync();
        var events = db.ExternalAccessTokenAuditEvents
            .Where(e => e.TokenId == created.Value.Item.TokenId)
            .OrderBy(e => e.Id)
            .ToList();
        Assert.AreEqual(3, events.Count);
        Assert.AreEqual(ExternalAccessTokenAuditEventType.Created, events[0].EventType);
        Assert.AreEqual(ExternalAccessTokenAuditEventType.Renamed, events[1].EventType);
        Assert.AreEqual(ExternalAccessTokenAuditEventType.Revoked, events[2].EventType);
    }

    private static ExternalAccessTokenCreateCommand Command(
        string name,
        IReadOnlyList<string>? scopes = null,
        IReadOnlyList<string>? workspaces = null,
        int? lifetimeDays = null)
        => new()
        {
            Name = name,
            Scopes = scopes ?? [ExternalTaskApiScopes.TasksRead],
            WorkspaceIds = workspaces ?? ["default"],
            LifetimeDays = lifetimeDays,
            OwnerUserId = Owner,
        };
}
