using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;

namespace CapabilityBrokerTests;

/// <summary>
/// 基于可信调用方身份的能力授权：RPC 可达 ≠ 获得桌面操作授权（计划 §7）。
///
/// 这批测试的重点是**默认拒绝**：任何拿不到可信身份的调用都必须被拒，
/// 且目录里**每一个**能力都要被同一条规则覆盖（逐个能力走一遍，防止某个能力漏网）。
/// </summary>
public sealed class TrustedCallerDesktopCapabilityAuthorizerTests
{
    private static readonly DesktopInstanceId DesktopId = new("desktop-1");

    [Fact]
    public async Task MissingIdentity_DeniesEveryCapabilityInTheCatalogue()
    {
        // 最强的一条：不逐能力挑样本，而是把目录走一遍——任何"忘了判身份"的分支都会在这里露出来。
        var authorizer = Create(identity: null);

        foreach (var descriptor in DesktopCapabilities.All)
        {
            var error = await AuthorizeAsync(authorizer, descriptor);

            Assert.NotNull(error);
            Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, error!.Code);
        }
    }

    [Fact]
    public async Task IdentityWithoutSession_Denies()
    {
        var authorizer = Create(new DesktopCallerIdentity
        {
            SessionId = "  ",
            AgentInstanceId = "agent-1",
            ExecutionId = "exec-1",
            UserId = "user-1",
        });

        var error = await AuthorizeAsync(authorizer, contexts());

        Assert.NotNull(error);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, error!.Code);
    }

    [Fact]
    public async Task ReadOnlyCapability_WithIdentity_IsAllowed()
    {
        var authorizer = Create(Identity());

        var error = await AuthorizeAsync(authorizer, contexts());

        Assert.Null(error);
    }

    [Fact]
    public async Task TrustedContextCapability_WithoutAgentIdentity_Denies()
    {
        var authorizer = Create(Identity() with { AgentInstanceId = null });

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.RequiresTrustedContext));

        Assert.NotNull(error);
        Assert.Contains("trusted agent context", error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutatingCapability_WithoutExecution_Denies()
    {
        // 变更类能力必须来自某次**可识别**的 Agent 执行：事后要能回答"是谁动的手"。
        var authorizer = Create(Identity() with { ExecutionId = null });

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.Mutating));

        Assert.NotNull(error);
        Assert.Contains("identifiable agent execution", error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MutatingCapability_WithFullExecution_IsAllowed()
    {
        var authorizer = Create(Identity());

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.Mutating));

        Assert.Null(error);
    }

    [Fact]
    public async Task SideEffectingCapability_WithoutAgentIdentity_Denies()
    {
        var authorizer = Create(Identity() with { AgentInstanceId = null });

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.HasSideEffects));

        Assert.NotNull(error);
    }

    [Fact]
    public async Task UserInteractionCapability_WithoutUserIdentity_Denies()
    {
        // 需要用户在场的能力（对话框/Picker）：弹窗必须能归因到人。
        var authorizer = Create(Identity() with { UserId = null });

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.RequiresUserInteraction));

        Assert.NotNull(error);
        Assert.Contains("user presence", error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UserInteractionCapability_WithUserIdentity_IsAllowed()
    {
        var authorizer = Create(Identity());

        var error = await AuthorizeAsync(authorizer, WithTrait(DesktopCapabilityTraits.RequiresUserInteraction));

        Assert.Null(error);
    }

    [Fact]
    public async Task Denial_DoesNotLeakPageContentOrCredentials()
    {
        var authorizer = Create(identity: null);

        var error = await AuthorizeAsync(authorizer, contexts());

        Assert.NotNull(error);
        Assert.DoesNotContain("http", error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancellation_PropagatesInsteadOfBeingReportedAsDenial()
    {
        var authorizer = Create(Identity());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 取消是同步抛出的（授权器不是 async 方法）：它必须原样传播，不能被折叠成"拒绝"。
        Assert.Throws<OperationCanceledException>(() =>
            authorizer.AuthorizeAsync(BuildContext(contexts()), cts.Token));
    }

    [Fact]
    public void Constructor_RejectsMissingProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new TrustedCallerDesktopCapabilityAuthorizer(null!));
    }

    private static DesktopCapabilityDescriptor contexts() =>
        DesktopCapabilities.All.Single(d => d.Capability == DesktopCapability.BrowserContexts);

    private static DesktopCapabilityDescriptor WithTrait(DesktopCapabilityTraits trait) =>
        DesktopCapabilities.All.First(d => (d.Traits & trait) != 0);

    private static DesktopCallerIdentity Identity() => new()
    {
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        ExecutionId = "exec-1",
        UserId = "user-1",
        WorkspaceId = "workspace-1",
    };

    private static TrustedCallerDesktopCapabilityAuthorizer Create(DesktopCallerIdentity? identity) =>
        new(new FixedIdentityProvider(identity));

    private static Task<DesktopCapabilityError?> AuthorizeAsync(
        TrustedCallerDesktopCapabilityAuthorizer authorizer, DesktopCapabilityDescriptor descriptor) =>
        authorizer.AuthorizeAsync(BuildContext(descriptor), CancellationToken.None).AsTask();

    private static DesktopCapabilityAuthorizationContext BuildContext(DesktopCapabilityDescriptor descriptor) =>
        new(
            DesktopId,
            new ConnectionGeneration(1),
            descriptor,
            DesktopCapabilityRequest.ForContexts(),
            new DesktopCallContext(DesktopId, OperationId.NewId(), DateTimeOffset.UtcNow.AddSeconds(30)));

    private sealed class FixedIdentityProvider : IDesktopCallerIdentityProvider
    {
        private readonly DesktopCallerIdentity? _identity;

        public FixedIdentityProvider(DesktopCallerIdentity? identity) => _identity = identity;

        public DesktopCallerIdentity? TryGetCurrent() => _identity;
    }
}
