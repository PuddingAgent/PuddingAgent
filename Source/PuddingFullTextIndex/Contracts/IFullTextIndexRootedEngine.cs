namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// **绑定在某个索引根上**的引擎实例：除构建/搜索外，额外提供 staged 供给（A2a/A22a）所需的**三件**事 ——
/// 「语料根 → 本索引根下的索引目录」映射、reader 缓存的**显式失效**，与索引**文档数探针**。
/// <para>
/// ① <see cref="ResolveIndexDirectory"/> 暴露的命名哈希规则，其**唯一实现**是
/// <c>FullTextIndexPaths.ResolveIndexDirectory</c>（A19 已收敛）：
/// 供给层（含 staged 切换）与 CLI 一律经它取目录名，不得复刻「<c>sha256(大写规范化全路径)</c>」——
/// 复刻即第二处真源（CLI 侧原有的 <c>SupplyScopeMirror.ResolveIndexDirectory</c> 已在 A19 删除）。
/// staged 供给因此不需要自己算目录名：staging 引擎按同一规则把语料根映射到 staging 根之下。
/// </para>
/// <para>
/// ② <see cref="InvalidateScope"/> 必须在**整目录替换之前**调用，理由是既有事实（非推测）：
/// 引擎用 <c>DirectoryReader.OpenIfChanged</c> 自刷新，而引擎自身的注释已承认
/// 「<c>OpenIfChanged</c> 未必可靠感知，陈旧 Reader 会继续看到过期文档」；
/// 另外 Windows 下未释放的 reader 文件句柄会阻止目录 <c>Move</c>（同卷重命名同样会被拒）。
/// 切换完成后应**再调用一次**，保证后续查询立刻看到新索引。
/// </para>
/// <para>
/// ⚠️ 为什么不直接把这些成员加到 <see cref="IFullTextSearchEngine"/>：该接口被 CLI 工程
/// （本切片红线：不可改）的实现/替身/测试共同实现，加成员会破坏其编译。
/// 因此以**新增**独立接缝的方式扩展，原接口语义保持不变。
/// </para>
/// </summary>
public interface IFullTextIndexRootedEngine : IFullTextSearchEngine
{
    /// <summary>
    /// 把语料根映射为**本索引根**下的索引目录绝对路径（目录可能尚不存在；映射规则与构建/搜索完全一致）。
    /// </summary>
    string ResolveIndexDirectory(string corpusRootPath);

    /// <summary>
    /// 失效该语料根对应的 reader/searcher 缓存。幂等：缓存里没有该项也算成功。
    /// </summary>
    void InvalidateScope(string corpusRootPath);

    /// <summary>
    /// 探测「语料根 → 本索引根下索引目录」的**文档数**（A22a R1）：供给的回归闸门用它回答
    /// 「staging 是不是近乎空的」这个体积答不了的问题（2026-09-25 事故：体积合规但只有 0/99 文档）。
    /// <para>
    /// 契约三态（实现必须区分，见 <see cref="IndexDocumentProbe"/>）：
    /// 目录不存在 ⇒ <c>Exists=false</c>；存在且可读 ⇒ <c>Documents</c>=真实篇数（<c>0</c> 合法）；
    /// 存在但读不出 ⇒ <c>Documents=null</c>（<b>不得</b>伪报 0）。
    /// </para>
    /// <para>
    /// 路径解析必须复用 <see cref="ResolveIndexDirectory"/> 的同一规则（单一真源），
    /// 探针只读：<b>不得</b>创建目录、不得写索引、不得改 reader 缓存（供 gate 在切换前调用）。
    /// </para>
    /// </summary>
    /// <param name="corpusRootPath">语料根目录（不是索引目录）。</param>
    IndexDocumentProbe ProbeDocuments(string corpusRootPath);
}
