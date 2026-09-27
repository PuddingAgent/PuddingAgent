using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-12 role slice: the permission vocabulary Core actually defines, and system-role immutability.</summary>
public sealed class RoleContractTests
{
    [Fact]
    public void PermissionVocabularyIsExactlyWhatCoresOwnRolesUse()
    {
        // Core 的唯一权威来源是它四个内置角色的 PermissionsJson；全仓没有任何检查引用 team:*/user:*。
        Assert.Equal(
            ["workspace:read", "workspace:write", "workspace:manage",
             "agent:run", "agent:manage",
             "template:read", "template:manage",
             "llm:read", "llm:manage"],
            RoleText.KnownPermissions);
        Assert.True(RoleText.IsKnownPermission("workspace:manage"));
        Assert.False(RoleText.IsKnownPermission("team:manage"));
        Assert.False(RoleText.IsKnownPermission("user:manage"));
        Assert.False(RoleText.IsKnownPermission("workspace.manage"));
        Assert.False(RoleText.IsKnownPermission(null));

        Assert.Contains("读工作区", RoleText.DescribePermission("workspace:read"), StringComparison.Ordinal);
        Assert.Equal("scope:write", RoleText.DescribePermission("scope:write"));
        Assert.Contains("没有任何一处引用", RoleText.TeamUserPermissionGap, StringComparison.Ordinal);
        Assert.Contains("不做白名单校验", RoleText.PermissionNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void RoleIdsStayConservativeAndNamesAreRequired()
    {
        Assert.Empty(RoleText.ValidateRoleId("workspace-editor"));
        Assert.Empty(RoleText.ValidateRoleId("role.v2_x"));
        Assert.Contains("不能为空", RoleText.ValidateRoleId(" ").Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", RoleText.ValidateRoleId("role/editor").Single(), StringComparison.Ordinal);
        Assert.NotEmpty(RoleText.ValidateRoleId(new string('a', 65)));
    }

    [Fact]
    public void ValidationCatchesUnknownPermissionsBeforeCoreStoresThem()
    {
        var edit = new RoleEdit("custom-role", "Custom", "desc", ["workspace:read"]);
        Assert.Empty(RoleText.Validate(edit, isCreate: false));
        Assert.Empty(RoleText.Validate(edit, isCreate: true));

        // 新建必须给出角色 ID；更新沿用已有 ID，所以只在校验新建时检查它。
        Assert.Contains("角色 ID", RoleText.Validate(edit with { RoleId = "" }, isCreate: true).Single(), StringComparison.Ordinal);
        Assert.Empty(RoleText.Validate(edit with { RoleId = "" }, isCreate: false));

        Assert.Contains("名称", RoleText.Validate(edit with { Name = " " }, isCreate: false).Single(), StringComparison.Ordinal);
        // Core 会把这个字符串原样存进 JSON，但没有任何检查会认它——界面先拦。
        var unknown = edit with { Permissions = ["workspace:read", "team:manage"] };
        Assert.Contains("team:manage", RoleText.Validate(unknown, isCreate: false).Single(), StringComparison.Ordinal);
        Assert.Empty(RoleText.Validate(edit with { Permissions = [] }, isCreate: false));
    }

    [Fact]
    public void PermissionNormalizationIsStableAndDeduplicated()
    {
        Assert.Equal(["agent:manage", "workspace:read"], RoleText.Normalize([" workspace:read ", "agent:manage", "workspace:read"]));
        // Ordinal 去重：大小写不同视为不同取值（Core 的字符串比较也是 Ordinal）。
        Assert.Equal(["AGENT:RUN", "agent:run"], RoleText.Normalize(["agent:run", "AGENT:RUN"]));
        Assert.Empty(RoleText.Normalize(null));
        Assert.Empty(RoleText.Normalize(["", "   "]));
    }

    [Fact]
    public void SystemRolesAreMarkedReadOnlyAndUnknownPermissionsAreVisible()
    {
        var system = new PermissionRole(1, "workspace-admin", "Workspace 管理员", "built in",
            ["workspace:manage", "workspace:read"], true, DateTimeOffset.UtcNow);
        Assert.True(system.IsSystemRole);
        Assert.False(system.IsEditable);
        Assert.False(system.HasUnknownPermission);
        Assert.Equal("系统内置", system.KindText);
        Assert.Contains("workspace:manage", system.PermissionsText, StringComparison.Ordinal);
        Assert.Equal("无权限", (system with { Permissions = [] }).PermissionsText);

        var custom = system with { IsSystemRole = false, Permissions = ["workspace:read", "team:manage"] };
        Assert.True(custom.IsEditable);
        Assert.True(custom.HasUnknownPermission, "未知权限必须被标出来，而不是当成有效权限");
        Assert.Contains("不可修改也不可删除", RoleText.SystemRoleNotice, StringComparison.Ordinal);
    }
}
