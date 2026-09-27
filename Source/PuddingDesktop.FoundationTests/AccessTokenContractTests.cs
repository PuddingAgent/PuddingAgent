using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-11 access tokens: the eight scopes, the one-time secret, and CAS/revoke semantics.</summary>
public sealed class AccessTokenContractTests
{
    private static ExternalApiStatus Status() => new(true, "https://example.invalid", true, 30, 365, 5);

    private static AccessTokenCreateRequest Request() =>
        new("ci-token", ["default"], ["tasks.read"], null);

    [Fact]
    public void ScopeWhitelistHasNoWildcardAndKeepsTheRealSemantics()
    {
        Assert.Equal(
            ["tasks.read", "tasks.write", "tasks.comment", "tasks.evaluate", "tasks.command",
             "workspaces.read", "agents.read", "messages.send"],
            AccessTokenText.Scopes);
        Assert.DoesNotContain("*", AccessTokenText.Scopes);
        Assert.True(AccessTokenText.IsKnownScope("tasks.command"));
        Assert.False(AccessTokenText.IsKnownScope("tasks.admin"));
        Assert.False(AccessTokenText.IsKnownScope("*"));
        // 文档化的不隐含关系必须体现在描述里，而不是让读者自己猜。
        Assert.Contains("不含 command", AccessTokenText.DescribeScope("tasks.write"), StringComparison.Ordinal);
        Assert.Contains("不改状态", AccessTokenText.DescribeScope("tasks.evaluate"), StringComparison.Ordinal);
        Assert.Contains("没有通配符", AccessTokenText.ScopeNotice, StringComparison.Ordinal);
        Assert.Contains("fail closed", AccessTokenText.ScopeNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateFormRequiresNameScopesWorkspacesAndInRangeLifetime()
    {
        Assert.Empty(AccessTokenText.Validate(Request(), Status()));
        Assert.Contains("名称", AccessTokenText.Validate(Request() with { Name = " " }, Status()).Single(), StringComparison.Ordinal);
        Assert.Contains("scope", AccessTokenText.Validate(Request() with { Scopes = [] }, Status()).Single(), StringComparison.Ordinal);
        Assert.Contains("未知 scope", AccessTokenText.Validate(Request() with { Scopes = ["tasks.admin"] }, Status()).Single(), StringComparison.Ordinal);
        Assert.Contains("工作区", AccessTokenText.Validate(Request() with { WorkspaceIds = [] }, Status()).Single(), StringComparison.Ordinal);
        // 显式越界由 Core 拒绝，表单先拦；留空用默认值。
        Assert.Contains("有效期", AccessTokenText.Validate(Request() with { LifetimeDays = 0 }, Status()).Single(), StringComparison.Ordinal);
        Assert.Contains("有效期", AccessTokenText.Validate(Request() with { LifetimeDays = 366 }, Status()).Single(), StringComparison.Ordinal);
        Assert.Empty(AccessTokenText.Validate(Request() with { LifetimeDays = 365 }, Status()));
    }

    [Fact]
    public void CreationErrorsAreTranslatedFromCoresCodes()
    {
        Assert.Contains("上限", AccessTokenText.DescribeCreateError("TooManyActiveTokens"), StringComparison.Ordinal);
        Assert.Contains("409", AccessTokenText.DescribeCreateError("TooManyActiveTokens"), StringComparison.Ordinal);
        Assert.Contains("未知 scope", AccessTokenText.DescribeCreateError("UnknownScope"), StringComparison.Ordinal);
        Assert.Contains("工作区", AccessTokenText.DescribeCreateError("UnknownWorkspace"), StringComparison.Ordinal);
        Assert.Contains("有效期", AccessTokenText.DescribeCreateError("LifetimeOutOfRange"), StringComparison.Ordinal);
        Assert.Equal("创建失败。", AccessTokenText.DescribeCreateError(null));
    }

    [Fact]
    public void StatusAndSecretNoticesStayHonest()
    {
        Assert.Equal("有效", AccessTokenText.DescribeStatus("active"));
        Assert.Equal("已过期", AccessTokenText.DescribeStatus("Expired"));
        Assert.Equal("已撤销", AccessTokenText.DescribeStatus("Revoked"));
        Assert.Equal("Owner 已停用", AccessTokenText.DescribeStatus("OwnerDisabled"));
        Assert.Equal("状态未知", AccessTokenText.DescribeStatus(null));
        Assert.True(AccessTokenText.IsKnownStatus("ownerdisabled"));

        Assert.Contains("出现一次", AccessTokenText.SecretOnceNotice, StringComparison.Ordinal);
        Assert.Contains("没有「取消撤销」", AccessTokenText.RevokeNotice, StringComparison.Ordinal);
        Assert.Contains("expectedVersion", AccessTokenText.VersionNotice, StringComparison.Ordinal);
        Assert.Contains("500", AccessTokenText.RevokeReasonNotice, StringComparison.Ordinal);
        Assert.Contains("仍然要求填写", AccessTokenText.RevokeReasonNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenSummaryExposesMetadataOnly()
    {
        var token = new AccessTokenSummary("tok", "kid", "pdt_v1_ab", "Name", "owner", 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, "", "", null,
            ["tasks.read"], ["default"], "Active");
        Assert.True(token.IsActive);
        Assert.Equal("有效", token.StatusText);
        Assert.Equal("从未使用", token.LastUsedText);
        Assert.Equal("", token.RevocationText);
        Assert.Equal("tasks.read", token.ScopesText);
        Assert.Equal("无 scope", (token with { Scopes = [] }).ScopesText);

        var revoked = token with
        {
            Status = "Revoked", RevokedAtUtc = DateTimeOffset.UtcNow, RevokedByUserId = "admin",
            RevocationReason = "rotated"
        };
        Assert.False(revoked.IsActive);
        Assert.Contains("撤销于", revoked.RevocationText, StringComparison.Ordinal);
        Assert.Contains("rotated", revoked.RevocationText, StringComparison.Ordinal);
        Assert.Contains("最后使用", (token with { LastUsedAtUtc = DateTimeOffset.UtcNow }).LastUsedText, StringComparison.Ordinal);

        var created = new AccessTokenCreated(token, "pdt_v1_plaintext");
        Assert.Equal("pdt_v1_plaintext", created.TokenText);
        // 模型里没有 hash 字段：结构上就无法把存储哈希带进界面。
        Assert.DoesNotContain("Hash", string.Join(",", typeof(AccessTokenSummary).GetProperties().Select(p => p.Name)));

        var page = new AccessTokenListPage([token], 41, 2, 20);
        Assert.Equal(3, page.PageCount);
        Assert.Equal("已启用", new ExternalApiStatus(true, "", false, 30, 365, 5).EnabledText);
        Assert.Contains("365", new ExternalApiStatus(true, "", false, 30, 365, 5).LifetimeText, StringComparison.Ordinal);
    }
}