using System.Text.Json;
using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using PuddingBrowser.Abstractions;
using PuddingBrowser.AgentTools;
using PuddingBrowser.Protocol;
using PuddingDesktop.Browser;
using PuddingHost.BrowserBridge;

namespace PuddingHost.Tests.BrowserBridge;

/// <summary>
/// D8 契约连通门禁：**真实生产者（Desktop 路由/结果适配器链接源码）→ wire DTO →
/// 真实消费者（RemoteBrowserRuntime/Context/Page）**。
///
/// 与两侧各自构造期望 DTO 的单测不同，这里不允许替身手工制作消费者期待的 JSON：
/// broker 只把路由产出的 `BrowserBridgeCommandResult` 原样返回，形状错就由真实消费者报错。
/// </summary>
public sealed class BrowserBridgeWireContractTests : IDisposable
{
    private readonly IDisposable _callerScope = BrowserCallerIdentityScope.Push(
        new BrowserCallerIdentity(BrowserCallerKind.Agent, "agent-test", "run-test"),
        null);

    public void Dispose() => _callerScope.Dispose();

    private sealed class RouteBroker(Func<BrowserBridgeCommand, Task<BrowserBridgeCommandResult?>> route)
        : IDesktopBrowserCommandBroker
    {
        public List<BrowserBridgeCommand> Commands { get; } = [];
        public bool IsDesktopConnected => true;

        public async Task<BrowserBridgeCommandResult> ExecuteAsync(
            BrowserBridgeCommand command,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return await route(command).ConfigureAwait(false)
                ?? new BrowserBridgeCommandResult
                {
                    OperationId = command.OperationId,
                    Success = false,
                    ErrorCode = BrowserBridgeErrorCodes.BrowserOperationNotSupported,
                    ErrorMessage = $"route has no response for '{command.Name}'",
                };
        }

        public Task CancelAsync(Guid operationId, CancellationToken cancellationToken) => Task.CompletedTask;
        public void HandleResult(Guid connectionId, long generation, BrowserBridgeCommandResult result) { }

        public void FailPendingForConnection(
            Guid connectionId, long generation, string errorCode, string message) { }
    }

    private static (RemoteBrowserRuntime Runtime, RouteBroker Broker) CreateChain(
        Func<DesktopCapabilityDescriptor, DesktopCapabilityRequest, Task<DesktopCapabilityResponse>> invoker)
    {
        var route = new BrowserBridgeExecutorRoute((descriptor, request, _, _) => invoker(descriptor, request));
        var broker = new RouteBroker(command => route.RouteAsync(command, CancellationToken.None));
        return (new RemoteBrowserRuntime(broker, callerIdentity: new BrowserCallerIdentityAccessor()), broker);
    }

    private static DesktopContexts TwoContexts() => new(
    [
        new DesktopContextInfo("ctx-a", DesktopContextTrust.AgentAuthorized,
        [
            new DesktopPageInfo(new DesktopPageTarget("ctx-a", "page-a1"), DesktopPageVersion.Require(1), title: "A1"),
            new DesktopPageInfo(new DesktopPageTarget("ctx-a", "page-a2"), DesktopPageVersion.Require(2), title: "A2"),
        ]),
        new DesktopContextInfo("ctx-b", DesktopContextTrust.AgentAuthorized,
        [
            new DesktopPageInfo(new DesktopPageTarget("ctx-b", "page-b1"), DesktopPageVersion.Require(3), title: "B1"),
        ]),
    ]);

    [Fact]
    public void BridgeContractRevision_IsThree_SoMismatchedBuildsCannotMixPayloads()
    {
        // D9：本次合同修复整体发布修订 3，两端严格相等；改动必须显式更新本断言。
        Assert.Equal(3, BrowserBridgeProtocol.CurrentVersion);
    }

    [Fact]
    public async Task ContextListAndGetInfo_AreConsumedByTheRealCoreRuntime()
    {
        var (runtime, _) = CreateChain((descriptor, _) => Task.FromResult(descriptor.Capability switch
        {
            DesktopCapability.BrowserContexts => DesktopCapabilityResponse.FromContexts(TwoContexts()),
            _ => DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.UnsupportedCapability($"unexpected '{descriptor.Name}'")),
        }));
        await using var _runtime = runtime;

        var contexts = await runtime.ListContextsAsync(CancellationToken.None);
        Assert.Equal(2, contexts.Count);
        Assert.Equal(2, contexts.Single(context => context.Id.Value == "ctx-a").PageCount);

        // 单个 context.getInfo：真实消费者按单对象解码（列表包装会在此失败）。
        var contextB = await runtime.GetContextAsync(new BrowserContextId("ctx-b"), CancellationToken.None);
        Assert.NotNull(contextB);
        Assert.Equal("ctx-b", contextB!.Id.Value);
        Assert.Equal(1, contextB.Info.PageCount);

        // 不存在的 context：稳定 not_found → 真实消费者返回 null，而不是"取列表第一项"。
        var missing = await runtime.GetContextAsync(new BrowserContextId("ctx-missing"), CancellationToken.None);
        Assert.Null(missing);
    }

    [Fact]
    public async Task PageList_IsScopedByContextAndNeverFetchesPerPageControl()
    {
        var (runtime, broker) = CreateChain((descriptor, _) => Task.FromResult(descriptor.Capability switch
        {
            DesktopCapability.BrowserContexts => DesktopCapabilityResponse.FromContexts(TwoContexts()),
            _ => DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.UnsupportedCapability($"unexpected '{descriptor.Name}'")),
        }));
        await using var _runtime = runtime;

        var context = await runtime.GetContextAsync(new BrowserContextId("ctx-a"), CancellationToken.None);
        Assert.NotNull(context);
        var pages = await context!.ListPagesAsync(CancellationToken.None);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Equal("ctx-a", page.ContextId.Value));
        // F4：发现不逐页取得页面控制（不得出现 page.getInfo）。
        Assert.DoesNotContain(broker.Commands, command => command.Name == BrowserBridgeCommandNames.PageGetInfo);
        Assert.Contains(broker.Commands, command => command.Name == BrowserBridgeCommandNames.PageList);
    }

    [Fact]
    public async Task EmptyClose_IsConsumedAsSuccessNotAsMissingPayload()
    {
        var (runtime, _) = CreateChain((descriptor, _) => Task.FromResult(descriptor.Capability switch
        {
            DesktopCapability.BrowserContextClose => DesktopCapabilityResponse.FromContextClosed(
                new DesktopContextClosed("ctx-a")),
            _ => DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.UnsupportedCapability($"unexpected '{descriptor.Name}'")),
        }));
        await using var _runtime = runtime;

        // 不抛 "returned no value"：空响应成功必须被真实消费者接受。
        await runtime.CloseContextAsync(new BrowserContextId("ctx-a"), CancellationToken.None);
    }

    [Fact]
    public async Task HistoryNavigation_IsConsumedAsTheSinglePageDescriptor()
    {
        var pageA1 = new DesktopPageState(
            new DesktopPageTarget("ctx-a", "page-a1"),
            new Uri("https://example.com/a1"),
            DesktopPageVersion.Require(2),
            DesktopPageReadiness.Complete,
            "A1");

        var (runtime, _) = CreateChain((descriptor, request) => Task.FromResult(descriptor.Capability switch
        {
            DesktopCapability.BrowserContexts => DesktopCapabilityResponse.FromContexts(TwoContexts()),
            DesktopCapability.WebViewPageState => DesktopCapabilityResponse.FromPageState(pageA1),
            DesktopCapability.WebViewNavigate => DesktopCapabilityResponse.FromNavigate(new NavigateResult(
                NavigateDisposition.Completed,
                new Uri("https://example.com/back"),
                DesktopPageVersion.Require(9),
                Ok: true,
                Title: "Back")),
            _ => DesktopCapabilityResponse.Failure(
                DesktopCapabilityError.UnsupportedCapability($"unexpected '{descriptor.Name}'")),
        }));
        await using var _runtime = runtime;

        var context = await runtime.GetContextAsync(new BrowserContextId("ctx-a"), CancellationToken.None);
        Assert.NotNull(context);
        var page = await context!.GetPageAsync(new PageId("page-a1"), CancellationToken.None);
        Assert.NotNull(page);
        Assert.Equal(2, page!.PageVersion);

        // back/forward/reload/stop 消费者是单页描述符；导航包装会在此解码失败。
        await page.GoBackAsync(CancellationToken.None);
        Assert.Equal(9, page.PageVersion);
        Assert.Equal("https://example.com/back", page.Info.Url);
    }
}
