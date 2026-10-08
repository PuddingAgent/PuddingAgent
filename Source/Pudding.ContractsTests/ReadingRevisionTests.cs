using Pudding.Contracts.Desktop;

namespace Pudding.ContractsTests;

/// <summary>
/// 修订标识（设计方案 §145）：绑定「页面版本 + 查询指纹 + 内容哈希」。
/// 不可比时必须能说出来（refresh_required），**绝不伪造变化**。
/// </summary>
public sealed class ReadingRevisionTests
{
    private static string Fingerprint => BrowserReadingRevision.QueryFingerprintOf("outline", null, null);

    [Fact]
    public void Revision_RoundTripsAndBindsVersionQueryAndContent()
    {
        var revision = BrowserReadingRevision.Create(4, Fingerprint, "hello");
        var wire = revision.Encode();

        Assert.StartsWith("r1-4-", wire, StringComparison.Ordinal);
        Assert.True(BrowserReadingRevision.TryParse(wire, out var parsed));

        Assert.Equal(4, parsed.PageVersion);
        Assert.Equal(Fingerprint, parsed.QueryFingerprint);
        Assert.True(parsed.HasSameContentAs("hello"));
        Assert.False(parsed.HasSameContentAs("hello "));
        Assert.False(parsed.HasSameContentAs(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("r1-4-abcdefgh")]              // 少一段
    [InlineData("r1-4-abcdefgh-ijklmnop-extra")]
    [InlineData("r2-4-abcdefgh-ijklmnop")]     // 未知方案版本
    [InlineData("r1-0-abcdefgh-ijklmnop")]     // 版本必须为正
    [InlineData("r1-4-ABCDEFGH-ijklmnop")]     // 大写十六进制不接受（编码始终小写）
    [InlineData("r1-4-abc-ijklmnop")]          // 指纹长度不对
    [InlineData("r1-x-abcdefgh-ijklmnop")]     // 版本不是数字
    [InlineData("r1-4-zzzzzzzz-ijklmnop")]     // 非十六进制
    public void MalformedRevision_IsRejected(string? wire)
    {
        Assert.False(BrowserReadingRevision.TryParse(wire, out _));
    }

    [Fact]
    public void Comparability_RequiresSameVersionAndSameQuery()
    {
        var revision = BrowserReadingRevision.Create(7, Fingerprint, "body");

        Assert.True(revision.IsComparableTo(7, Fingerprint));

        // 版本不同 ⇒ 谈不了"变化"（内容变了是必然的，说了等于没说）。
        Assert.False(revision.IsComparableTo(8, Fingerprint));

        // 查询不同 ⇒ 差异来自查询而不是页面。
        Assert.False(revision.IsComparableTo(7, BrowserReadingRevision.QueryFingerprintOf("full", null, null)));
        Assert.False(revision.IsComparableTo(7, BrowserReadingRevision.QueryFingerprintOf("outline", "p1-v7-n2", null)));
    }

    [Fact]
    public void ContentHash_IsStableAcrossProcesses()
    {
        // 同一内容必须永远得到同一修订（用 string.GetHashCode 会按进程加盐 ⇒ 重启后误报"变了"）。
        var first = BrowserReadingRevision.Create(3, Fingerprint, "same text").Encode();
        var second = BrowserReadingRevision.Create(3, Fingerprint, "same text").Encode();

        Assert.Equal(first, second);
        Assert.NotEqual(first, BrowserReadingRevision.Create(3, Fingerprint, "other text").Encode());
    }

    [Fact]
    public void QueryFingerprint_DistinguishesModeScopeAndFields()
    {
        var baseline = BrowserReadingRevision.QueryFingerprintOf("outline", null, null);

        Assert.Equal(baseline, BrowserReadingRevision.QueryFingerprintOf("outline", null, []));
        Assert.NotEqual(baseline, BrowserReadingRevision.QueryFingerprintOf("interactive", null, null));
        Assert.NotEqual(baseline, BrowserReadingRevision.QueryFingerprintOf("outline", "p1-v2-n3", null));
        Assert.NotEqual(baseline, BrowserReadingRevision.QueryFingerprintOf("outline", null, ["domText"]));
        // 字段顺序不影响指纹（同一组字段就是同一查询）。
        Assert.Equal(
            BrowserReadingRevision.QueryFingerprintOf("outline", null, ["a", "b"]),
            BrowserReadingRevision.QueryFingerprintOf("outline", null, ["a", "b"]));
    }

    [Fact]
    public void Create_RejectsNonsenseInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BrowserReadingRevision.Create(0, Fingerprint, "x"));
        Assert.Throws<ArgumentException>(() => BrowserReadingRevision.Create(1, "  ", "x"));
    }

    [Fact]
    public void RefreshRequired_SaysReReadInsteadOfRetrying()
    {
        var error = new Pudding.Contracts.DesktopCapabilityError(
            Pudding.Contracts.DesktopCapabilityErrorCode.RefreshRequired, "baseline not comparable");

        Assert.Equal("refresh_required", error.WireCode);
        Assert.False(error.Retryable);
        Assert.False(error.MayHaveSideEffects);
    }
}
