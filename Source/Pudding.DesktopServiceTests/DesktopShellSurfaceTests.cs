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
        var surface = new DesktopShellSurface(facilities);

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
        var surface = new DesktopShellSurface(facilities);

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
        var surface = new DesktopShellSurface(facilities);

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
        var surface = new DesktopShellSurface(facilities);

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
        var surface = new DesktopShellSurface(facilities);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => surface.RequestDialogAsync(
            Context, new DesktopDialogRequest("标题", "内容"), cts.Token));
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