using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace DesktopServiceTests;

/// <summary>Shell 设施适配层：预算纵深防御、取消归一、异常不越界（平台无关，可脱 UI 测试）。</summary>
public sealed class DesktopShellSurfaceTests
{
    private static readonly DesktopCallContext Context = new(
        new DesktopInstanceId("desktop-1"),
        new OperationId("op-1"),
        DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task ClipboardOverBudget_IsTruncatedAndFlaggedEvenIfTheFacilityIgnoresTheBudget()
    {
        var facilities = new FakeFacilities
        {
            // 设施"不守规矩"：无视预算回带超长内容。
            ClipboardHandler = (_, _) => Task.FromResult(CapabilityResult<DesktopClipboardContent>.Success(
                new DesktopClipboardContent(new string('x', 5_000), truncated: false))),
        };
        var surface = Create(facilities);

        var result = await surface.ReadClipboardAsync(
            Context, new ClipboardReadRequest(64), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Equal(64, result.Value.Length);
        Assert.True(result.Value.Truncated);
        // 内容不进日志：形状里没有原文。
        Assert.DoesNotContain("xxxx", result.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledDialog_StaysAResultNotAFailure()
    {
        var facilities = new FakeFacilities
        {
            DialogHandler = (_, _) => Task.FromResult(CapabilityResult<DesktopDialogResult>.Success(
                new DesktopDialogResult(DesktopDialogChoice.Cancel))),
        };
        var surface = Create(facilities);

        var result = await surface.RequestDialogAsync(
            Context, new DesktopDialogRequest("标题", "内容", DesktopDialogButtons.OkCancel), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.True(result.Value.Canceled);
        Assert.False(result.Value.IsAffirmative);
    }

    [Fact]
    public async Task FacilityFailuresArePropagatedUnchanged()
    {
        var facilities = new FakeFacilities
        {
            FilePickerHandler = (_, _) => Task.FromResult(
                CapabilityResult<DesktopFilePickerResult>.Failure(
                    DesktopCapabilityError.UiUnavailable("no window"))),
        };
        var surface = Create(facilities);

        var result = await surface.RequestFilePickerAsync(
            Context, new DesktopFilePickerRequest("选择文件"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.UiUnavailable, result.Error!.Code);
    }

    [Fact]
    public async Task ThrownExceptionsBecomeInternalErrorsWithoutLeakingTheMessage()
    {
        var facilities = new FakeFacilities
        {
            ClipboardHandler = (_, _) => throw new InvalidOperationException("clipboard had SECRET-CONTENT"),
        };
        var surface = Create(facilities);

        var result = await surface.ReadClipboardAsync(
            Context, new ClipboardReadRequest(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
        Assert.DoesNotContain("SECRET-CONTENT", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBeingFoldedIntoAFailure()
    {
        using var cts = new CancellationTokenSource();
        var facilities = new FakeFacilities
        {
            DialogHandler = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        var surface = Create(facilities);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => surface.RequestDialogAsync(
            Context, new DesktopDialogRequest("标题", "内容"), cts.Token));
    }

    [Fact]
    public async Task NotificationNotShown_StaysASuccessfulResult()
    {
        // 「没弹出来」是结果而不是失败：调用方要能区分「通知没到」与「能力不可用」。
        var host = new FakeHostFacilities
        {
            NotificationHandler = (_, _) => Task.FromResult(
                CapabilityResult<DesktopNotificationResult>.Success(new DesktopNotificationResult(false, null))),
        };
        var surface = Create(hostFacilities: host);

        var result = await surface.ShowNotificationAsync(
            Context, new DesktopNotificationRequest("标题", "内容"), CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.False(result.Value.Shown);
    }

    [Fact]
    public async Task NotificationForwardsTheRequestUnchanged()
    {
        DesktopNotificationRequest? seen = null;
        var host = new FakeHostFacilities
        {
            NotificationHandler = (request, _) =>
            {
                seen = request;
                return Task.FromResult(
                    CapabilityResult<DesktopNotificationResult>.Success(new DesktopNotificationResult(true, "toast-1")));
            },
        };
        var surface = Create(hostFacilities: host);
        var request = new DesktopNotificationRequest("任务完成", "共 3 项", DesktopNotificationPriority.High);

        var result = await surface.ShowNotificationAsync(Context, request, CancellationToken.None);

        Assert.True(result.Value.Shown);
        Assert.Equal("toast-1", result.Value.NotificationId);
        Assert.Same(request, seen);
    }

    [Fact]
    public async Task ShellStatusIsForwardedWithoutInventingAutomationOrPageCount()
    {
        // 适配层**不补齐**自动化状态/页面数：那是 DesktopService 依自身权威状态覆盖的，
        // 两处都补会让状态互相漂移。
        var reported = new DesktopShellStatus(
            DesktopWindowState.HiddenToTray, trayVisible: true, DesktopAutomationState.Free, openPageCount: 0);
        var host = new FakeHostFacilities
        {
            StatusHandler = (_, _) => Task.FromResult(CapabilityResult<DesktopShellStatus>.Success(reported)),
        };
        var surface = Create(hostFacilities: host);

        var result = await surface.GetShellStatusAsync(Context, CancellationToken.None);

        Assert.False(result.IsFailure);
        Assert.Same(reported, result.Value);
        Assert.Equal(DesktopWindowState.HiddenToTray, result.Value.WindowState);
        Assert.True(result.Value.TrayVisible);
    }

    [Fact]
    public async Task HostFacilityExceptionBecomesInternalErrorWithoutLeakingTheMessage()
    {
        var host = new FakeHostFacilities
        {
            StatusHandler = (_, _) => throw new InvalidOperationException("tray state for SECRET-PAGE"),
        };
        var surface = Create(hostFacilities: host);

        var result = await surface.GetShellStatusAsync(Context, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InternalError, result.Error!.Code);
        Assert.DoesNotContain("SECRET-PAGE", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostFacilityCancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        var host = new FakeHostFacilities
        {
            NotificationHandler = (_, _) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        };
        var surface = Create(hostFacilities: host);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => surface.ShowNotificationAsync(
            Context, new DesktopNotificationRequest("标题", "内容"), cts.Token));
    }

    private static DesktopShellSurface Create(
        FakeFacilities? facilities = null,
        FakeHostFacilities? hostFacilities = null) =>
        new(facilities ?? new FakeFacilities(), hostFacilities ?? new FakeHostFacilities());

    private sealed class FakeHostFacilities : IDesktopShellHostFacilities
    {
        public Func<DesktopNotificationRequest, CancellationToken, Task<CapabilityResult<DesktopNotificationResult>>>? NotificationHandler { get; set; }

        public Func<DesktopCallContext, CancellationToken, Task<CapabilityResult<DesktopShellStatus>>>? StatusHandler { get; set; }

        public Task<CapabilityResult<DesktopNotificationResult>> ShowNotificationAsync(
            DesktopCallContext context, DesktopNotificationRequest request, CancellationToken cancellationToken) =>
            NotificationHandler?.Invoke(request, cancellationToken)
            ?? Task.FromResult(CapabilityResult<DesktopNotificationResult>.Success(new DesktopNotificationResult(true, "n-1")));

        public Task<CapabilityResult<DesktopShellStatus>> GetShellStatusAsync(
            DesktopCallContext context, CancellationToken cancellationToken) =>
            StatusHandler?.Invoke(context, cancellationToken)
            ?? Task.FromResult(CapabilityResult<DesktopShellStatus>.Success(new DesktopShellStatus(
                DesktopWindowState.Visible, trayVisible: true, DesktopAutomationState.Free, openPageCount: 0)));
    }

    private sealed class FakeFacilities : IDesktopShellFacilities
    {
        public Func<DesktopDialogRequest, CancellationToken, Task<CapabilityResult<DesktopDialogResult>>>? DialogHandler { get; set; }

        public Func<DesktopFilePickerRequest, CancellationToken, Task<CapabilityResult<DesktopFilePickerResult>>>? FilePickerHandler { get; set; }

        public Func<ClipboardReadRequest, CancellationToken, Task<CapabilityResult<DesktopClipboardContent>>>? ClipboardHandler { get; set; }

        public Task<CapabilityResult<DesktopDialogResult>> RequestDialogAsync(
            DesktopCallContext context, DesktopDialogRequest request, CancellationToken cancellationToken) =>
            DialogHandler?.Invoke(request, cancellationToken)
            ?? Task.FromResult(CapabilityResult<DesktopDialogResult>.Success(new DesktopDialogResult(DesktopDialogChoice.Ok)));

        public Task<CapabilityResult<DesktopFilePickerResult>> RequestFilePickerAsync(
            DesktopCallContext context, DesktopFilePickerRequest request, CancellationToken cancellationToken) =>
            FilePickerHandler?.Invoke(request, cancellationToken)
            ?? Task.FromResult(CapabilityResult<DesktopFilePickerResult>.Success(new DesktopFilePickerResult(false, [@"C:\picked.txt"])));

        public Task<CapabilityResult<DesktopClipboardContent>> ReadClipboardAsync(
            DesktopCallContext context, ClipboardReadRequest request, CancellationToken cancellationToken) =>
            ClipboardHandler?.Invoke(request, cancellationToken)
            ?? Task.FromResult(CapabilityResult<DesktopClipboardContent>.Success(new DesktopClipboardContent("short", false)));
    }
}