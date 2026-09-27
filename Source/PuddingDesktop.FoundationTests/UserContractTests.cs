using PuddingDesktop.Foundation;

namespace PuddingDesktop.FoundationTests;

/// <summary>DS-12 user slice: password confirmation, unique email and the last-Admin rule.</summary>
public sealed class UserContractTests
{
    private static UserCreate Create() => new(
        "alice", "Alice", "alice@example.invalid", "Alice A", "SimpleUser", "s3cret-pass", "s3cret-pass");

    [Fact]
    public void WhenUserTypesDefineWhatCoreAccepts()
    {
        Assert.Equal(["Admin", "SimpleUser"], UserContractsText.UserTypes);
        Assert.Contains("管理员", UserContractsText.DescribeUserType("admin"), StringComparison.Ordinal);
        Assert.Contains("普通用户", UserContractsText.DescribeUserType("SimpleUser"), StringComparison.Ordinal);
        Assert.Equal("类型未知", UserContractsText.DescribeUserType(null));
        Assert.Equal("SomethingNew", UserContractsText.DescribeUserType("SomethingNew"));
        Assert.True(UserContractsText.IsKnownUserType("simpleuser"));
        Assert.False(UserContractsText.IsKnownUserType("Owner"));
    }

    [Fact]
    public void CreateRequiresAMatchingConfirmedPasswordOfCoreLength()
    {
        Assert.Empty(UserContractsText.Validate(Create()));
        Assert.Equal(6, UserContractsText.MinimumPasswordLength);

        var short_ = UserContractsText.Validate(Create() with { Password = "abc", ConfirmPassword = "abc" });
        Assert.Contains("不得少于 6 位", short_.Single(), StringComparison.Ordinal);

        // 二次确认是界面要求：Core 只收一个密码字段，但填错必须拦下。
        var mismatch = UserContractsText.Validate(Create() with { ConfirmPassword = "other-pass" });
        Assert.Contains("不一致", mismatch.Single(), StringComparison.Ordinal);

        Assert.Contains("不能为空", UserContractsText.Validate(Create() with { Password = "", ConfirmPassword = "" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void AccountFieldsAreCheckedBeforeCoreWritesAnything()
    {
        var errors = UserContractsText.Validate(Create() with
        {
            UserId = "bad id", Username = " ", Email = "not-an-email", UserType = "Owner"
        });
        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, error => error.Contains("UserId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("用户名", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("邮箱", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("UserType", StringComparison.Ordinal));

        Assert.Empty(UserContractsText.ValidateUserId("alice.v2_x-1"));
        Assert.Contains("不能为空", UserContractsText.ValidateUserId(" ").Single(), StringComparison.Ordinal);
        Assert.Contains("只能包含", UserContractsText.ValidateUserId("a/b").Single(), StringComparison.Ordinal);
        Assert.True(UserContractsText.IsPlausibleEmail("a@b.c"));
        Assert.False(UserContractsText.IsPlausibleEmail("@b.c"));
        Assert.False(UserContractsText.IsPlausibleEmail("a@"));
        Assert.False(UserContractsText.IsPlausibleEmail(null));
    }

    [Fact]
    public void EditAndPasswordChangeReuseTheSameRules()
    {
        var edit = new UserMetaEdit("alice", "Alice", "alice@example.invalid", "Alice A", "Admin", false);
        Assert.Empty(UserContractsText.Validate(edit));
        Assert.Contains("UserType", UserContractsText.Validate(edit with { UserType = "Owner" }).Single(), StringComparison.Ordinal);
        Assert.Contains("邮箱", UserContractsText.Validate(edit with { Email = "nope" }).Single(), StringComparison.Ordinal);

        var change = new UserPasswordChange("alice", "newpass1", "newpass1");
        Assert.Empty(UserContractsText.Validate(change));
        Assert.Contains("不得少于 6 位", UserContractsText.Validate(change with { NewPassword = "abc", ConfirmPassword = "abc" }).Single(), StringComparison.Ordinal);
        Assert.Contains("不一致", UserContractsText.Validate(change with { ConfirmPassword = "different" }).Single(), StringComparison.Ordinal);
        Assert.Contains("缺少用户", UserContractsText.Validate(change with { UserId = "" }).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoticesStateTheRealGuarantees()
    {
        Assert.Contains("不回显密码", UserContractsText.PasswordNotice, StringComparison.Ordinal);
        Assert.Contains("至少保留一个 Admin", UserContractsText.LastAdminNotice, StringComparison.Ordinal);
        Assert.Contains("全量替换", UserContractsText.RoleReplacementNotice, StringComparison.Ordinal);
        Assert.Contains("唯一", UserContractsText.EmailConflictNotice, StringComparison.Ordinal);

        var user = new AppUserAccount(1, "alice", "Alice", "a@b.c", "Alice A", "Admin", true,
            ["workspace-admin"], DateTimeOffset.UtcNow, "");
        Assert.True(user.IsAdmin);
        Assert.Equal("已启用", user.StateText);
        Assert.Equal("Admin（管理员）", user.UserTypeText);
        Assert.Equal("workspace-admin", user.RolesText);
        Assert.Equal("未分配角色", (user with { RoleIds = [] }).RolesText);
        Assert.Equal("已停用", (user with { IsEnabled = false }).StateText);
    }
}
