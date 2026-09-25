using PuddingHost.Hosting;

namespace PuddingHost.Tests.Hosting;

/// <summary>
/// U4-7：<see cref="FullTextIndexSupplyOptions.MinRebuildInterval"/> 的消费点
/// （<see cref="IndexPrebuildFreshness"/>，纯函数，注入时钟）。
/// </summary>
public sealed class IndexPrebuildFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>没有索引 ⇒ 必须建（与间隔无关）。</summary>
    [Fact]
    public void Should_Rebuild_When_There_Is_No_Index()
    {
        Assert.True(IndexPrebuildFreshness.ShouldRebuild(
            hasIndex: false,
            indexLastWriteUtc: Now,
            nowUtc: Now,
            minRebuildInterval: TimeSpan.FromHours(12)));
    }

    /// <summary>已有索引且足够新 ⇒ 跳过。</summary>
    [Fact]
    public void Should_Skip_When_The_Index_Is_Fresher_Than_The_Interval()
    {
        Assert.False(IndexPrebuildFreshness.ShouldRebuild(
            hasIndex: true,
            indexLastWriteUtc: Now - TimeSpan.FromHours(11),
            nowUtc: Now,
            minRebuildInterval: TimeSpan.FromHours(12)));
    }

    /// <summary>已有索引但超过间隔 ⇒ 重建。</summary>
    [Fact]
    public void Should_Rebuild_When_The_Index_Is_Older_Than_The_Interval()
    {
        Assert.True(IndexPrebuildFreshness.ShouldRebuild(
            hasIndex: true,
            indexLastWriteUtc: Now - TimeSpan.FromHours(13),
            nowUtc: Now,
            minRebuildInterval: TimeSpan.FromHours(12)));
    }

    /// <summary>间隔为 0（= 每次都重建）⇒ 不跳过。</summary>
    [Fact]
    public void Should_Rebuild_When_The_Interval_Is_Zero()
    {
        Assert.True(IndexPrebuildFreshness.ShouldRebuild(
            hasIndex: true,
            indexLastWriteUtc: Now,
            nowUtc: Now,
            minRebuildInterval: TimeSpan.Zero));
    }

    /// <summary>读不到索引时间（<see cref="DateTimeOffset.MinValue"/>）⇒ 年龄极大 ⇒ 重建（安全方向）。</summary>
    [Fact]
    public void Should_Rebuild_When_The_Index_Timestamp_Is_Unknown()
    {
        Assert.True(IndexPrebuildFreshness.ShouldRebuild(
            hasIndex: true,
            indexLastWriteUtc: DateTimeOffset.MinValue,
            nowUtc: Now,
            minRebuildInterval: TimeSpan.FromHours(12)));
    }
}
