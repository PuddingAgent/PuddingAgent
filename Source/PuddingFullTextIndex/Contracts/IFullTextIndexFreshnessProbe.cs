namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// 「语料根 → 该 scope 索引新鲜度」的**只读**探针（A23）。
/// <para>
/// 为什么是**独立**接口，而不是给 <see cref="IFullTextSearchEngine"/> 或
/// <see cref="IFullTextIndexRootedEngine"/> 加成员：这两个接口被 CLI 工程与多处替身/桩共同实现，
/// 加成员会破坏它们的编译。A19（命名哈希单一真源）与 A2a（<see cref="IFullTextIndexRootedEngine"/>）
/// 已按同一理由以「新增独立接缝」的方式扩展 —— 本次沿用该模式，原接口语义逐字不变。
/// </para>
/// <para>
/// <b>实现方硬约束</b>（与 <see cref="IFullTextIndexRootedEngine.ProbeDocuments"/> 同）：
/// 只读 —— <b>不得</b>创建目录、不得写索引、不得改 reader/searcher 缓存；
/// 「语料根 → 索引目录」一律经命名哈希的单一真源解析（<c>FullTextIndexPaths.ResolveIndexDirectory</c>），
/// <b>不得</b>复刻规则。
/// </para>
/// </summary>
public interface IFullTextIndexFreshnessProbe
{
    /// <summary>
    /// 读某语料根对应索引的新鲜度。三态必须可区分（见 <see cref="FullTextIndexFreshnessState"/>）；
    /// 「读不出」一律如实上报为读不出，<b>不得</b>伪报时间或伪报为「刚建过」。
    /// </summary>
    /// <param name="corpusRootPath">语料根目录（不是索引目录）。</param>
    FullTextIndexFreshness ProbeFreshness(string corpusRootPath);
}
