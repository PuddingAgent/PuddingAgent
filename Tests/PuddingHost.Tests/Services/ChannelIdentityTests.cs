using PuddingCode.Platform;

namespace PuddingHost.Tests.Services;

/// <summary>
/// 渠道身份只描述「谁在说话」。绑定结果决定是否附带内部身份，渠道本身不携带任何权限。
/// </summary>
public sealed class ChannelIdentityTests
{
    [Fact]
    public void UnboundSenderCarriesOnlyTheExternalIdentifier()
    {
        var identity = new ChannelIdentity("dingtalk", "member-1");

        Assert.Equal("[dingtalk user=member-1]", identity.Prefix);
        Assert.Null(identity.UserId);
        Assert.Null(identity.Role);
    }

    [Fact]
    public void BoundSenderCarriesPuddingUserAndRole()
    {
        var identity = new ChannelIdentity("feishu", "ou_abc", "admin", "admin");

        Assert.Equal("[feishu user=ou_abc pudding=admin role=admin]", identity.Prefix);
    }

    [Fact]
    public void MetadataKeysAreStable()
    {
        Assert.Equal("channel_identity_channel", ChannelIdentityMetadata.Channel);
        Assert.Equal("channel_identity_external_user_id", ChannelIdentityMetadata.ExternalUserId);
        Assert.Equal("channel_identity_pudding_user_id", ChannelIdentityMetadata.UserId);
        Assert.Equal("channel_identity_pudding_role", ChannelIdentityMetadata.Role);
    }
}
