using PuddingCode.Models;
using PuddingCode.Tools;
using PuddingRuntime.Services.Tools;

namespace PuddingRuntimeTests.Services;

/// <summary>
/// 发现 W 回归：授权主体身份必须由单一函数派生。
/// 事故形态：Feishu 命令通道写 gateway:H(channelType, externalUserId)，而 Agent turn 的
/// 执行主体是 fabric:H(kind, id)。派生输入与前缀都不同 ⇒ 永不匹配 ⇒ 人工
/// /authorize 兜底在该通道永久无效（控制反转），而票据引导文案正指引用户走这条路。
/// 修复后两侧统一调用 MessagePrincipalIdentity.FromSender。
/// </summary>
[TestClass]
public sealed class MessagePrincipalIdentityTests
{
    [TestMethod]
    public void FromSender_IsDeterministic_AndLocksWireFormat()
    {
        var first = MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, "ou_feishu_1");
        var second = MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, "ou_feishu_1");

        Assert.AreEqual(first, second, "同一端点必须派生出同一主体标识");
        Assert.AreEqual("fabric:bfb46155131094fe7db6e1e74d9e173b", first, "派生格式即落盘授权的主体键，变更会静默作废既有授权");
        Assert.AreEqual(39, first.Length, "应为 'fabric:'(7) + 32 位十六进制");
    }

    [TestMethod]
    public void FromSender_SeparatesKindBoundary()
    {
        Assert.AreNotEqual(
            MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, "agent-1"),
            MessagePrincipalIdentity.FromSender(MessageEndpointKinds.Agent, "1"),
            "kind 与 id 必须共享边界，否则同一 id 在不同 kind 下会互相冒充");

        // 反例：若退化为 kind+id 裸拼接，这两者会碰撞。
        Assert.AreNotEqual(
            MessagePrincipalIdentity.FromSender("user", "agent-1"),
            MessagePrincipalIdentity.FromSender("useragent", "-1"));
    }

    [TestMethod]
    public void FromSender_RejectsEmptyInputs()
    {
        Assert.ThrowsExactly<ArgumentException>(() => MessagePrincipalIdentity.FromSender(" ", "x"));
        Assert.ThrowsExactly<ArgumentException>(() => MessagePrincipalIdentity.FromSender("user", " "));
    }

    /// <summary>
    /// 硬要求：身份归一化不得过头。授权按主体标识精确 Ordinal 匹配，
    /// 因此另一个用户写入的 grant 绝不得命中（发现 W 修复不得引入越权）。
    /// </summary>
    [TestMethod]
    public async Task Grant_WrittenForOneUser_DoesNotAuthorizeAnotherUser()
    {
        var svc = new InMemoryToolAuthorizationService();
        var userA = ContextFor(MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, "ou_a"));
        var userB = ContextFor(MessagePrincipalIdentity.FromSender(MessageEndpointKinds.User, "ou_b"));

        await svc.ApplyCommandAsync(
            new ToolAuthorizationCommand
            {
                RawText = "/authorize sample_high session",
                Action = ToolAuthorizationAction.Authorize,
                ToolId = "sample_high",
                Scope = ToolAuthorizationScope.Session,
            },
            userA);

        Assert.IsTrue((await svc.CheckAsync(userA, Descriptor)).IsAuthorized, "写入者本人必须命中");
        Assert.IsFalse((await svc.CheckAsync(userB, Descriptor)).IsAuthorized,
            "另一个用户写入的授权不得命中（越权负例）");

        static ToolAuthorizationContext ContextFor(string userId) => new()
        {
            WorkspaceId = "workspace-1",
            SessionId = "session-1",
            AgentInstanceId = "agent-1",
            UserId = userId,
            ToolId = "sample_high",
        };
    }

    private static readonly ToolDescriptor Descriptor = new()
    {
        ToolId = "sample_high",
        Name = "Sample high",
        Description = "High-risk descriptor used by principal identity authorization tests.",
        PermissionLevel = ToolPermissionLevel.High,
    };
}

/// <summary>
/// 防止派生再次分裂：任何通道若重新引入自己的身份前缀，本测试必须失败。
/// </summary>
[TestClass]
public sealed class PrincipalIdentityCallSiteGuardTests
{
    [TestMethod]
    public void PrincipalIdentity_Derivation_IsSingleSourced()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            Assert.Inconclusive("Repository root not resolvable from " + AppContext.BaseDirectory);
            return;
        }

        var ingress = File.ReadAllText(Path.Combine(
            root, "Source", "PuddingHost", "Services", "MessageGatewayIngress.cs"));
        var dispatcher = File.ReadAllText(Path.Combine(
            root, "Source", "PuddingRuntime", "Services", "Messaging", "MessageDeliveryDispatcher.cs"));

        Assert.IsFalse(ingress.Contains("gateway-user", StringComparison.Ordinal),
            "Feishu 命令侧不得再使用通道专属身份前缀（发现 W 回归）");
        Assert.IsFalse(ingress.Contains("$\"gateway:", StringComparison.Ordinal),
            "Feishu 命令侧不得再自行派生 gateway: 身份");
        Assert.IsTrue(ingress.Contains("MessagePrincipalIdentity.FromSender", StringComparison.Ordinal),
            "Feishu 命令侧必须调用统一派生函数");

        Assert.IsFalse(dispatcher.Contains("StableGatewayUserId", StringComparison.Ordinal),
            "存在通道专属派生的残留函数 StableGatewayUserId");
        Assert.IsFalse(dispatcher.Contains("StableMessageFabricUserId", StringComparison.Ordinal),
            "存在平行派生的残留函数 StableMessageFabricUserId（应全部走 MessagePrincipalIdentity）");
        Assert.IsTrue(dispatcher.Contains("MessagePrincipalIdentity.FromSender", StringComparison.Ordinal),
            "消息执行侧必须调用统一派生函数");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Agents.md"))
                && Directory.Exists(Path.Combine(dir.FullName, "Source")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
