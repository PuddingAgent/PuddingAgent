using Pudding.DesktopConnection;

namespace DesktopConnectionTests;

public sealed class ByteBudgetTests
{
    [Fact]
    public void TryReserve_RespectsCapacity()
    {
        var budget = new ByteBudget(100);

        Assert.True(budget.TryReserve(60));
        Assert.Equal(60, budget.Used);
        Assert.False(budget.TryReserve(60));
        Assert.True(budget.TryReserve(40));
        Assert.Equal(100, budget.Used);

        budget.Release(40);
        Assert.Equal(60, budget.Used);
        Assert.True(budget.TryReserve(40));
    }

    [Fact]
    public void ShrinkTo_LowersCapacityButAllowsCurrentUsageToDrain()
    {
        var budget = new ByteBudget(1000);
        Assert.True(budget.TryReserve(800));

        budget.ShrinkTo(100);

        Assert.Equal(100, budget.Capacity);
        Assert.Equal(800, budget.Used);
        Assert.False(budget.TryReserve(1));

        budget.Release(800);
        Assert.True(budget.TryReserve(100));
        Assert.False(budget.TryReserve(1));
    }

    [Fact]
    public async Task ReserveAsync_WaitsForReleaseAndKeepsFifoOrder()
    {
        var budget = new ByteBudget(100);
        Assert.True(budget.TryReserve(100));

        var first = budget.ReserveAsync(60, CancellationToken.None);
        var second = budget.ReserveAsync(50, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        budget.Release(100);

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(60, budget.Used);

        // 剩余 40 不够 50：第二个等待者必须继续排队（不允许超发）。
        Assert.False(second.IsCompleted);

        budget.Release(60);
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(50, budget.Used);
    }

    [Fact]
    public async Task ReserveAsync_ReturnsFalseWhenCancelled()
    {
        var budget = new ByteBudget(10);
        Assert.True(budget.TryReserve(10));

        using var cancellation = new CancellationTokenSource();
        var pending = budget.ReserveAsync(5, cancellation.Token);
        await Task.Delay(30);
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(10, budget.Used);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteBudget(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ByteBudget(-1));
    }

    [Fact]
    public void Release_ClampsAtZero()
    {
        var budget = new ByteBudget(10);
        budget.Release(100);
        Assert.Equal(0, budget.Used);
    }
}
