using Grpc.Core;
using Pudding.CapabilityBroker;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Proto = Pudding.Rpc.Protocol.V1;

namespace CapabilityBrokerAspNetCoreTests;

/// <summary>
/// 授权在**真实通道**上的行为（不是单测替身）。本轮把产品授权器从 `DenyAll` 换成可信身份门禁，
/// 因此必须验证两件只有端到端才看得见的事：
/// ① 无身份 ⇒ 被拒，**且命令根本不下发**（授权必须发生在发送之前——否则"拒绝"只是桌面侧白挨一次命令）；
/// ② 有可信身份 ⇒ 命令正常下发、结果正常回填。
/// </summary>
public sealed class TrustedCallerAuthorizerOverTheWireTests
{
    private const string DesktopIdValue = "desk-test";

    [Fact]
    public async Task WithoutIdentity_DeniesAndNeverSendsTheCommand()
    {
        var identities = new MutableIdentityProvider();
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-denied"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);

        // 关键断言：拒绝发生在发送之前 —— Desktop 侧不应看到任何帧。
        // 读取窗口内没有帧时 harness 会以 Cancelled 结束这次流读取，因此把它折成 null。
        async Task<Proto.CoreFrame?> ReadWithinWindowAsync()
        {
            try
            {
                return await harness.ReadAsync(call);
            }
            catch (RpcException)
            {
                return null;
            }
        }

        Assert.Null(await ReadWithinWindowAsync());
    }

    [Fact]
    public async Task WithTrustedIdentity_SendsTheCommandAndCompletesTheCall()
    {
        var identities = new MutableIdentityProvider { Current = Identity() };
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-allowed"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var commandFrame = await harness.ReadAsync(call, frame => frame.Command is not null, "navigate command");
        Assert.Equal("webview.navigate", commandFrame.Command.Capability);
        Assert.Equal("op-allowed", commandFrame.Command.OperationId);

        await call.RequestStream.WriteAsync(new Proto.DesktopFrame
        {
            Result = new Proto.OperationResult
            {
                OperationId = "op-allowed",
                Generation = 1,
                Navigate = new Proto.NavigateOutcome
                {
                    Disposition = Proto.NavigateDisposition.Completed,
                    CurrentUrl = "https://example.com/done",
                    PageVersion = 4,
                },
            },
        });

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSuccess);
        Assert.Equal(NavigateDisposition.Completed, result.Value.Disposition);
    }

    [Fact]
    public async Task IdentityWithoutExecution_DeniesMutatingCapabilityBeforeSending()
    {
        // 身份在、但缺"某次可识别的 Agent 执行" ⇒ 变更类能力仍必须被拒（审计要求）。
        var identities = new MutableIdentityProvider
        {
            Current = Identity() with { ExecutionId = null },
        };
        await using var harness = await StartAsync(identities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        var session = harness.Broker.Sessions.Single();
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-no-exec"), DateTimeOffset.UtcNow.AddSeconds(30));

        var pending = session.NavigateAsync(
            new NavigateRequest(new DesktopPageTarget("ctx-1", "page-1"), new Uri("https://example.com/a")),
            context);

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.Unauthorized, result.Error!.Code);

        async Task<Proto.CoreFrame?> ReadWithinWindowAsync()
        {
            try
            {
                return await harness.ReadAsync(call);
            }
            catch (RpcException)
            {
                return null;
            }
        }

        Assert.Null(await ReadWithinWindowAsync());
    }

    /// <summary>窄端口覆盖的九项浏览器能力（用于把握手声明与授予上限都放开到这一组）。</summary>
    private const DesktopCapability NineBrowserCapabilities =
        DesktopCapability.WebViewNavigate
        | DesktopCapability.WebViewExecuteJavascript
        | DesktopCapability.WebViewPageState
        | DesktopCapability.BrowserContexts
        | DesktopCapability.BrowserTabs
        | DesktopCapability.BrowserSnapshot
        | DesktopCapability.BrowserLocate
        | DesktopCapability.BrowserInteract
        | DesktopCapability.BrowserWaitFor;

    private static async Task<CapabilityHostHarness> StartAsync(
        IDesktopCallerIdentityProvider identities,
        DesktopCapability grantable = DesktopCapability.None) =>
        await CapabilityHostHarness.StartAsync(
            configure: builder =>
            {
                builder.Authorizer = new TrustedCallerDesktopCapabilityAuthorizer(identities);
                if (grantable != DesktopCapability.None)
                {
                    // 能力集合要在**两处**放开：授予上限（策略）与 Desktop 的握手声明；
                    // 只放开一处会在本地就被剪掉，命令根本不上线（这正是本测试第一版的失败原因）。
                    builder.Grantable = grantable;
                }
            });

    [Fact]
    public async Task NarrowBrowserPort_RoutesEveryOperationThroughTheRealChannel()
    {
        // 切片 D 的窄端口不是纸面接口：把会话**当作端口**用，逐个操作验证它真的产生了对应的线上命令。
        // 九个方法的期望线名取自能力目录（wire 真源），因此这条测试同时钉住"端口 ↔ 目录"的一致性。
        var identities = new MutableIdentityProvider { Current = Identity() };
        await using var harness = await StartAsync(identities, NineBrowserCapabilities);

        var call = harness.Connect();
        await call.RequestStream.WriteAsync(CapabilityHostHarness.Hello(DesktopIdValue, declared: NineBrowserCapabilities));
        await harness.ReadAsync(call, frame => frame.HelloAck is not null, "hello_ack");
        IDesktopBrowserCapabilitySurface surface = harness.Broker.Sessions.Single();
        var target = new DesktopPageTarget("ctx-1", "page-1");
        var context = new DesktopCallContext(
            new DesktopInstanceId(DesktopIdValue), new OperationId("op-port"), DateTimeOffset.UtcNow.AddSeconds(30));
        var version = DesktopPageVersion.Require(1);

        // 每个操作必须用**各自的 operation id**：同 id 不同 payload 会被 broker 判为重复请求而拒绝
        // （这条不变式本测试第一版就撞上了，是真实契约而不是测试瑕疵）。
        static DesktopCallContext Ctx(string operationId) =>
            new(new DesktopInstanceId(DesktopIdValue), new OperationId(operationId), DateTimeOffset.UtcNow.AddSeconds(30));

        // 注意：单连接**在途额度是 8**（`CapabilityChannelOptions.MaxInFlightPerConnection`），
        // 而这些操作在本测试里都不回结果 ⇒ 只能连发 ≤8 个（第 9 个会被判超额度，会话进入 Faulted）。
        // `webview.execute_javascript` 已在既有的 `CancelRequest_TravelsOverTheWire` 里验证过命令上线；
        // `browser.wait_for` 由真实端点探针覆盖，故这里不再重复占用额度。
        var expected = new (string WireName, Func<Task<DesktopCapabilityError?>> Invoke)[]
        {
            ("browser.contexts", () => ErrorOf(surface.GetContextsAsync(Ctx("op-contexts")))),
            ("webview.page_state", () => ErrorOf(surface.GetPageStateAsync(target, Ctx("op-page-state")))),
            ("browser.tabs", () => ErrorOf(surface.TabsAsync(new BrowserTabsRequest(target, DesktopTabAction.Activate, version), Ctx("op-tabs")))),
            ("webview.navigate", () => ErrorOf(surface.NavigateAsync(new NavigateRequest(target, new Uri("https://example.com/a")), Ctx("op-navigate")))),
            ("browser.snapshot", () => ErrorOf(surface.SnapshotAsync(new BrowserSnapshotRequest(target, version), Ctx("op-snapshot")))),
            ("browser.locate", () => ErrorOf(surface.LocateAsync(new BrowserLocateRequest(target, new DesktopLocator(DesktopLocatorKind.Css, "a")), Ctx("op-locate")))),
            ("browser.interact", () => ErrorOf(surface.InteractAsync(new BrowserInteractRequest(target, DesktopInteractionAction.Click, version, new DesktopLocator(DesktopLocatorKind.Css, "button")), Ctx("op-interact")))),
        };

        foreach (var (wireName, invoke) in expected)
        {
            var pending = invoke();

            Proto.CoreFrame frame;
            try
            {
                // 用**会抛**的读取重载：它在窗口内等到匹配帧或超时抛错，但**不会取消整条流**
                //（可空重载在超时时取消调用，会把会话打成 Faulted，后续操作全部报 NotConnected）。
                frame = await harness.ReadAsync(call, f => f.Command is not null, $"{wireName} command");
            }
            catch (Exception ex)
            {
                var error = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Fail($"{wireName} 没有产生命令帧（{ex.GetType().Name}）；本地终态={error?.Code} — {error?.Message}");
                return;
            }

            Assert.Equal(wireName, frame.Command.Capability);
        }
    }

    private static async Task<DesktopCapabilityError?> ErrorOf<T>(Task<CapabilityResult<T>> call)
    {
        var result = await call;
        return result.IsFailure ? result.Error : null;
    }

    private static DesktopCallerIdentity Identity() => new()
    {
        SessionId = "session-1",
        AgentInstanceId = "agent-1",
        ExecutionId = "exec-1",
        UserId = "user-1",
    };

    private sealed class MutableIdentityProvider : IDesktopCallerIdentityProvider
    {
        public DesktopCallerIdentity? Current { get; set; }

        public DesktopCallerIdentity? TryGetCurrent() => Current;
    }
}
