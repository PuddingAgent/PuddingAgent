namespace Pudding.Contracts.Desktop;

/// <summary>
/// 读取结果的**修订标识**（设计方案 §145：「diff 是只读观察优化，绑定 baseline 和同一文档；
/// 基线缺失返回 <c>refresh_required</c>，**不伪造变化**」）。
///
/// 它同时绑定三件事，缺一不可：
/// ① **页面版本**：跨版本的比较毫无意义（内容变了是必然的，说了等于没说）；
/// ② **查询指纹**（模式/作用域/要了哪些字段）：不同查询之间的"变化"是查询差异，不是页面差异；
/// ③ **内容哈希**：同版本同查询下，内容是否真的变了。
///
/// 线格式：<c>r1-{pageVersion}-{queryFp8}-{contentHash8}</c>。
/// 载体是**调用方上次看到的内容**，因此比较是本地纯计算，不需要服务端保存基线。
/// </summary>
public readonly record struct BrowserReadingRevision
{
    private const string Prefix = "r1";

    private BrowserReadingRevision(long pageVersion, string queryFingerprint, string contentHash)
    {
        PageVersion = pageVersion;
        QueryFingerprint = queryFingerprint;
        ContentHash = contentHash;
    }

    public long PageVersion { get; }

    /// <summary>查询指纹（模式 + 作用域 + 字段白名单）。</summary>
    public string QueryFingerprint { get; }

    /// <summary>内容哈希。</summary>
    public string ContentHash { get; }

    public static BrowserReadingRevision Create(long pageVersion, string queryFingerprint, string? content)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);

        return new BrowserReadingRevision(pageVersion, queryFingerprint, StableHash.Short(content ?? string.Empty));
    }

    /// <summary>编码成对调用方不透明的线格式。</summary>
    public string Encode() => $"{Prefix}-{PageVersion}-{QueryFingerprint}-{ContentHash}";

    /// <summary>严格解析：任何偏差都返回 <c>false</c>（不猜测、不修补）。</summary>
    public static bool TryParse(string? wire, out BrowserReadingRevision revision)
    {
        revision = default;
        if (string.IsNullOrWhiteSpace(wire))
        {
            return false;
        }

        var parts = wire.Trim().Split('-');
        if (parts.Length != 4
            || !string.Equals(parts[0], Prefix, StringComparison.Ordinal)
            || !long.TryParse(parts[1], out var pageVersion)
            || pageVersion <= 0
            || !IsHash(parts[2])
            || !IsHash(parts[3]))
        {
            return false;
        }

        revision = new BrowserReadingRevision(pageVersion, parts[2], parts[3]);
        return true;
    }

    /// <summary>是否与「这个版本 + 这组查询」可比（不可比 ⇒ 必须重新读取，不能谈"变化"）。</summary>
    public bool IsComparableTo(long pageVersion, string queryFingerprint) =>
        PageVersion == pageVersion
        && string.Equals(QueryFingerprint, queryFingerprint, StringComparison.Ordinal);

    /// <summary>内容是否与当时一致（仅在可比的前提下有意义）。</summary>
    public bool HasSameContentAs(string? content) =>
        string.Equals(ContentHash, StableHash.Short(content ?? string.Empty), StringComparison.Ordinal);

    /// <summary>查询指纹：把影响读取结果的输入（模式/作用域/字段）规范化后取短哈希。</summary>
    public static string QueryFingerprintOf(
        string mode, string? scopeRef, IEnumerable<string>? fields)
    {
        var canonical = string.Join(
            '\u001f',
            mode ?? string.Empty,
            scopeRef ?? string.Empty,
            fields is null ? string.Empty : string.Join(',', fields));

        return StableHash.Short(canonical);
    }

    private static bool IsHash(string value) =>
        value.Length == 8 && value.All(Uri.IsHexDigit);

    public override string ToString() => $"revision v{PageVersion} ({ContentHash})";
}
