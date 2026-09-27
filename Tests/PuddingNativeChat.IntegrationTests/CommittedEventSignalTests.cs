using PuddingPlatform.Services;

namespace PuddingNativeChat.IntegrationTests;

public sealed class CommittedEventSignalTests
{
    [Fact]
    public async Task BroadcastRetainsHeadAndDoesNotWakeFutureCursorOrOtherConversation()
    {
        var signal = new CommittedEventSignal();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        signal.Signal("a", 3);
        await signal.WaitForChangeAsync("a", 2, timeout.Token);
        var one = signal.WaitForChangeAsync("a", 3, timeout.Token).AsTask();
        var two = signal.WaitForChangeAsync("a", 3, timeout.Token).AsTask();
        var future = signal.WaitForChangeAsync("a", 8, timeout.Token).AsTask();
        var other = signal.WaitForChangeAsync("b", 0, timeout.Token).AsTask();
        signal.Signal("a", 2); Assert.False(one.IsCompleted);
        signal.Signal("a", 4); await Task.WhenAll(one, two).WaitAsync(timeout.Token);
        Assert.False(future.IsCompleted); Assert.False(other.IsCompleted);
        signal.Signal("a", 9); await future.WaitAsync(timeout.Token);
        timeout.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => other);
    }

    [Fact]
    public async Task CancellingSubscriberDoesNotCancelOtherSubscribers()
    {
        var signal = new CommittedEventSignal();
        using var cancelled = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var one = signal.WaitForChangeAsync("a", 0, cancelled.Token).AsTask();
        var two = signal.WaitForChangeAsync("a", 0, timeout.Token).AsTask();
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        signal.Signal("a", 1); await two.WaitAsync(timeout.Token);
    }
}
