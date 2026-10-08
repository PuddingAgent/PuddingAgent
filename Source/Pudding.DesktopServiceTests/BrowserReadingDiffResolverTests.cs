using Pudding.Contracts;
using Pudding.Contracts.Desktop;
using Pudding.DesktopService;

namespace Pudding.DesktopServiceTests;

/// <summary>
/// 读取修订与行级差异的判定（设计方案 §145）。判定只有一份：
/// 基线在**真正读到内容的进程**里，因此"能不能比、比出来是什么"必须在这儿一次说清。
/// </summary>
public sealed class BrowserReadingDiffResolverTests
{
    private static readonly DesktopPageTarget Page = new("ctx-1", "page-1");

    private static DesktopSnapshotOptions Options(string? since = null, string? scope = null) =>
        new(sinceRevision: since, scopeRef: scope);

    private static string RevisionOf(BrowserReadingBaselineStore store, string? content, DesktopSnapshotOptions? options = null)
    {
        var outcome = BrowserReadingDiffResolver.Resolve(
            store, Page, options ?? Options(), DesktopPageVersion.Require(3), content);
        Assert.False(outcome.IsFailure);
        return outcome.Revision!;
    }

    [Fact]
    public void WithoutSinceRevision_ItOnlyReportsAndRemembers()
    {
        var store = new BrowserReadingBaselineStore();
        var outcome = BrowserReadingDiffResolver.Resolve(store, Page, Options(), DesktopPageVersion.Require(3), "a\nb");

        Assert.False(outcome.IsFailure);
        Assert.NotNull(outcome.Revision);
        Assert.Null(outcome.Changed);       // 没比较就不能声称"没变"
        Assert.Null(outcome.Diff);
        Assert.Equal(1, store.Count);       // 记住了，下次的比较才有基线
    }

    [Fact]
    public void SameContent_ReportsNoChange_AndDifferentContent_ReportsTheDiff()
    {
        var store = new BrowserReadingBaselineStore();
        var revision = RevisionOf(store, "a\nb\nc");

        var same = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: revision), DesktopPageVersion.Require(3), "a\nb\nc");
        Assert.False(same.IsFailure);
        Assert.False(same.Changed);
        Assert.NotNull(same.Diff);
        Assert.True(same.Diff!.IsEmpty);

        var changed = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: revision), DesktopPageVersion.Require(3), "a\nb\nc\nd");
        Assert.False(changed.IsFailure);
        Assert.True(changed.Changed);
        Assert.Equal(["d"], changed.Diff!.Added);
        Assert.Equal(1, changed.Diff.AddedCount);
    }

    [Fact]
    public void MalformedRevision_IsAnArgumentError_NotAChange()
    {
        var store = new BrowserReadingBaselineStore();
        var outcome = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: "garbage"), DesktopPageVersion.Require(3), "a");

        Assert.True(outcome.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.InvalidRequest, outcome.Failure!.Code);
    }

    [Fact]
    public void IncomparableRevision_SaysRefreshRequired_InsteadOfInventingAChange()
    {
        var store = new BrowserReadingBaselineStore();
        var revision = RevisionOf(store, "a\nb");

        // 同一版本但**读取形状不同**（作用域变了）⇒ 不可比：差异来自查询而不是页面。
        var otherShape = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: revision, scope: "p1-v3-n9"), DesktopPageVersion.Require(3), "a\nb\nc");

        Assert.True(otherShape.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.RefreshRequired, otherShape.Failure!.Code);

        // 版本也变了 ⇒ 同样不可比。
        var otherVersion = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: revision), DesktopPageVersion.Require(4), "a\nb\nc");

        Assert.True(otherVersion.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.RefreshRequired, otherVersion.Failure!.Code);
    }

    [Fact]
    public void MissingOrEvictedBaseline_SaysRefreshRequired_AndStillRemembersThisRead()
    {
        // 基线只留最近 2 份：第三次读取后最早的已被淘汰。
        var store = new BrowserReadingBaselineStore(capacityPerPage: 2);
        var first = RevisionOf(store, "v1");
        RevisionOf(store, "v2");
        RevisionOf(store, "v3");

        var outcome = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: first), DesktopPageVersion.Require(3), "v4");

        // 基线缺失 ⇒ 明确"重新读取"，**不伪造变化**。
        Assert.True(outcome.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.RefreshRequired, outcome.Failure!.Code);

        // 调用方照做（重新读一次、不带 since）⇒ 建立新基线；之后比较就能成立。
        var fresh = RevisionOf(store, "v4");
        var comparison = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: fresh), DesktopPageVersion.Require(3), "v4");
        Assert.False(comparison.IsFailure);
        Assert.False(comparison.Changed);
    }

    [Fact]
    public void PageWithoutLiveVersion_CannotCompare()
    {
        var store = new BrowserReadingBaselineStore();

        // 只读不比较：仍然可以（没有版本就不给修订，也不谈变化）。
        var readOnly = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(), DesktopPageVersion.Unknown, "a");
        Assert.False(readOnly.IsFailure);
        Assert.Null(readOnly.Revision);

        // 要比较 ⇒ 明确拒绝。
        var compare = BrowserReadingDiffResolver.Resolve(
            store, Page, Options(since: "r1-3-abcdefgh-ijklmnop"), DesktopPageVersion.Unknown, "a");
        Assert.True(compare.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.RefreshRequired, compare.Failure!.Code);
    }

    [Fact]
    public void BaselinesAreIsolatedPerPage()
    {
        var store = new BrowserReadingBaselineStore();
        var other = new DesktopPageTarget("ctx-1", "page-2");
        var revision = RevisionOf(store, "a\nb");

        // 同一份修订标识在**别的页面**上没有基线 ⇒ 不能说"没变"。
        var outcome = BrowserReadingDiffResolver.Resolve(
            store, other, Options(since: revision), DesktopPageVersion.Require(3), "a\nb");

        Assert.True(outcome.IsFailure);
        Assert.Equal(DesktopCapabilityErrorCode.RefreshRequired, outcome.Failure!.Code);
    }
}
