using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;
using Pudding.DesktopSurface.Browser;
using PuddingBrowser.Abstractions;

namespace Pudding.DesktopSurface.BrowserTests;

/// <summary>
/// 组装层本身<b>没有逻辑</b>，唯一可能的缺陷是「把某个成员接到了错误的协作者」——
/// 这类缺陷编译期查不出来，运行期表现为「能力能调用但做错事」。
///
/// 这里用「Shell 侧哨兵」把它变成可判定的：所有 Shell 设施都返回带 <c>SHELL-CANARY</c> 的错误，于是
/// · Shell 的 5 项**必须**带回哨兵（证明接到了 Shell 设施）；
/// · 浏览器的 9 项**必须不**带回哨兵（证明没接到 Shell 设施）。
/// 浏览器 9 项各自的映射语义由既有映射测试覆盖，这里只钉「没接错线」。
/// </summary>
public sealed class DesktopSurfaceCompositionTests
{
    private const string ShellCanary = "SHELL-CANARY";

    private static readonly DesktopCallContext Context = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task ShellMembers_ReachTheShellSurface()
    {
        var composition = Create();

        var calls = new (string Name, Func<Task<string?>> Call)[]
        {
            ("dialog", ErrorOf(() => composition.RequestDialogAsync(
                Context, new DesktopDialogRequest("t", "m"), CancellationToken.None))),
            ("file_picker", ErrorOf(() => composition.RequestFilePickerAsync(
                Context, new DesktopFilePickerRequest("t"), CancellationToken.None))),
            ("clipboard", ErrorOf(() => composition.ReadClipboardAsync(
                Context, new ClipboardReadRequest(), CancellationToken.None))),
            ("notification", ErrorOf(() => composition.ShowNotificationAsync(
                Context, new DesktopNotificationRequest("t", "m"), CancellationToken.None))),
            ("shell_status", ErrorOf(() => composition.GetShellStatusAsync(
                Context, CancellationToken.None))),
        };

        foreach (var (name, call) in calls)
        {
            var message = await call();
            Assert.NotNull(message);
            Assert.Contains(ShellCanary, message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task BrowserMembers_DoNotReachTheShellSurface()
    {
        var composition = Create();
        var target = new DesktopPageTarget("ctx-1", "page-1");

        var calls = new (string Name, Func<Task<string?>> Call)[]
        {
            ("navigate", ErrorOf(() => composition.NavigateAsync(
                Context, new NavigateRequest(target, new Uri("https://example.test/")), CancellationToken.None))),
            ("execute_javascript", ErrorOf(() => composition.ExecuteJavascriptAsync(
                Context, new JavascriptRequest(target, "1+1"), CancellationToken.None))),
            ("page_state", ErrorOf(() => composition.GetPageStateAsync(
                Context, target, CancellationToken.None))),
            ("tabs", ErrorOf(() => composition.TabsAsync(
                Context, new BrowserTabsRequest(target, DesktopTabAction.Activate, DesktopPageVersion.Require(1)), CancellationToken.None))),
            ("contexts", ErrorOf(() => composition.GetContextsAsync(Context, CancellationToken.None))),
            ("wait_for", ErrorOf(() => composition.WaitForAsync(
                Context, new BrowserWaitForRequest(target, new DesktopWaitCondition(DesktopWaitConditionKind.UrlPattern, "/done")), CancellationToken.None))),
            ("interact", ErrorOf(() => composition.InteractAsync(
                Context, new BrowserInteractRequest(target, DesktopInteractionAction.Click, DesktopPageVersion.Require(1)), CancellationToken.None))),
            ("locate", ErrorOf(() => composition.LocateAsync(
                Context, new BrowserLocateRequest(target, new DesktopLocator(DesktopLocatorKind.Css, "body")), CancellationToken.None))),
            ("snapshot", ErrorOf(() => composition.SnapshotAsync(
                Context, new BrowserSnapshotRequest(target, DesktopPageVersion.Require(1)), CancellationToken.None))),
        };

        foreach (var (name, call) in calls)
        {
            // 浏览器侧「失败/抛出」都算没接错：它的错误语义由各映射测试钉住。
            var message = await IgnoringBrowserFailuresAsync(call);
            Assert.True(
                message is null || !message.Contains(ShellCanary, StringComparison.Ordinal),
                $"{name} 接到了 Shell 设施（哨兵外泄）");
        }
    }

    [Fact]
    public async Task Constructor_RejectsMissingCollaborators()
    {
        var browser = new BrowserRuntimeDesktopSurface(new FakeRuntime(), new FakeTargets());
        var shell = new DesktopShellSurface(new CanaryFacilities(), new CanaryHostFacilities());

        Assert.Throws<ArgumentNullException>(() => new DesktopSurfaceComposition(null!, shell));
        Assert.Throws<ArgumentNullException>(() => new DesktopSurfaceComposition(browser, null!));
    }

    [Fact]
    public void Composition_IsTheSingleSurfaceSeamDesktopServiceConsumes()
    {
        // 组装结果必须真的是 DesktopService 的接缝类型（而不是看起来像的某个替身）。
        Assert.IsAssignableFrom<IDesktopUiSurface>(Create());
    }

    private static DesktopSurfaceComposition Create()
    {
        var browser = new BrowserRuntimeDesktopSurface(new FakeRuntime(), new FakeTargets());
        var shell = new DesktopShellSurface(new CanaryFacilities(), new CanaryHostFacilities());
        return new DesktopSurfaceComposition(browser, shell);
    }

    private static Func<Task<string?>> ErrorOf<T>(Func<Task<CapabilityResult<T>>> call) =>
        async () =>
        {
            var result = await call();
            return result.IsFailure ? result.Error!.Message : null;
        };

    private static async Task<string?> IgnoringBrowserFailuresAsync(Func<Task<string?>> call)
    {
        try
        {
            return await call();
        }
        catch
        {
            // 假运行时的 NotSupportedException 也算「没走 Shell」；真实映射的异常语义由映射测试覆盖。
            return null;
        }
    }

    private sealed class CanaryFacilities : IDesktopShellFacilities
    {
        public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
            DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<DesktopDialogResult>());

        public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
            DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<DesktopFilePickerResult>());

        public Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
            DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<DesktopClipboardContent>());
    }

    private sealed class CanaryHostFacilities : IDesktopShellHostFacilities
    {
        public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
            DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<DesktopNotificationResult>());

        public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
            DesktopCallContext context, CancellationToken cancellationToken) =>
            Task.FromResult(Failure<DesktopShellStatus>());
    }

    private static CapabilityResult<T> Failure<T>() =>
        CapabilityResult<T>.Failure(DesktopCapabilityError.Internal(ShellCanary));

    private sealed class FakeTargets : IDesktopBrowserTargetRegistry
    {
        public DesktopContextTrust TrustFor(string contextId) => DesktopContextTrust.AgentAuthorized;

        public bool IsAgentTarget(string contextId, string pageId) => true;

        public (string ContextId, string PageId)? ActivePage => null;
    }

    private sealed class FakeRuntime : IBrowserRuntime
    {
        public BrowserRuntimeState State => BrowserRuntimeState.Ready;

        public Task<IBrowserContext> CreateContextAsync(BrowserContextOptions options, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IBrowserContext?> GetContextAsync(BrowserContextId id, CancellationToken ct) =>
            Task.FromResult<IBrowserContext?>(null);

        public Task<IReadOnlyList<BrowserContextInfo>> ListContextsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BrowserContextInfo>>([]);

        public Task CloseContextAsync(BrowserContextId id, CancellationToken ct) => throw new NotSupportedException();

        public IAsyncEnumerable<BrowserEvent> WatchEventsAsync(BrowserEventFilter filter, CancellationToken ct) =>
            AsyncEnumerable.Empty<BrowserEvent>();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
