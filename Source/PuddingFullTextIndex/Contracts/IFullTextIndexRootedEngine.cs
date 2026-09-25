namespace PuddingFullTextIndex.Contracts;

/// <summary>
/// **绑定在某个索引根上**的引擎实例：除构建/搜索外，额外提供 staged 供给（A2a）所需的两件事 ——
/// 「语料根 → 本索引根下的索引目录」映射，与 reader 缓存的**显式失效**。
/// <para>
/// ① <see cref="ResolveIndexDirectory"/> 是索引目录命名哈希的**单一真源**：
/// 供给层（含 staged 切换）一律不得复刻「<c>sha256(大写规范化全路径)</c>」规则 ——
/// 它依赖引擎的路径规范化语义，复刻即第三处真源（CLI 侧已有一份 <c>SupplyScopeMirror</c>，
/// 登记为后续切片单独处理，本切片不动 CLI）。
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
}
