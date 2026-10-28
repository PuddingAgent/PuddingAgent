using PuddingRuntime.Tools.BuiltIns.Search;

namespace PuddingRuntimeTests.Tools;

/// <summary>
/// 索引作用域上溯解析的回归（2026-10-28，W1）。
///
/// 为什么值得单独钉死：这段解析决定「窄范围检索能不能用上索引」。
/// 索引按注册项目根建，而引擎要求 HasIndex(该目录)；没有上溯时，任何向下收窄的目录
/// 都会退化成托管扫描并撞 2000 文件上限（2026-10-28 实测两次）。解析写错的两个典型后果：
/// ① 明明祖先有索引却仍报 has no index yet（等于没修）；
/// ② 过于激进地向上爬到盘根，把无关的大索引当成覆盖（假阳性召回）。
/// 这两个方向都必须有断言兜住。
/// </summary>
[TestClass]
public sealed class IndexScopeResolverTests
{
    private static string Root { get; set; } = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        Root = Path.Combine(Path.GetTempPath(), "pudding-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结论。
        }
    }

    [TestMethod]
    public void Resolve_RequestedDirectoryIndexed_ReturnsSameRootWithoutSubFilter()
    {
        var dir = Path.Combine(Root, "project");
        var scope = IndexScopeResolver.Resolve(dir, HasIndex(dir), Root);

        Assert.IsNotNull(scope);
        Assert.AreEqual(Normalize(dir), scope!.Value.IndexRoot);
        Assert.IsNull(scope.Value.SubDirectoryFilter, "本身就是索引根时不应产生子目录过滤");
    }

    [TestMethod]
    public void Resolve_OnlyAncestorIndexed_ReturnsAncestorAndRelativeSubPath()
    {
        var project = Path.Combine(Root, "project");
        var deep = Path.Combine(project, "Source", "Services", "Storage");

        var scope = IndexScopeResolver.Resolve(deep, HasIndex(project), Root);

        Assert.IsNotNull(scope, "祖先有索引时必须解析成功——否则窄范围检索仍然用不上索引");
        Assert.AreEqual(Normalize(project), scope!.Value.IndexRoot);
        Assert.AreEqual(
            Path.Combine("Source", "Services", "Storage"),
            scope.Value.SubDirectoryFilter,
            "子目录过滤必须是相对索引根的相对路径，引擎按相对路径做前缀比较");
    }

    /// <summary>
    /// 生产部署形态：索引 scope 在工作区之外（本仓：scope=D:\CodeProject\...，工作区=D:\Data\workspaces\default）。
    /// 两者互不包含 ⇒ 边界对本次请求无意义，上溯必须照常工作。
    /// 缺陷形态（2026-10-28 宿主实测）：缺“边界归属判断”，第一次迭代就 return null，
    /// 仓库根可查（engineMs=12）而其子目录 Source/PuddingPlatform 仍报 has no index yet。
    /// </summary>
    [TestMethod]
    public void Resolve_RequestOutsideBoundary_StillResolvesIndexedAncestor()
    {
        var indexedRoot = Path.Combine(Root, "code", "Repo");
        var requested = Path.Combine(indexedRoot, "Source", "PuddingPlatform");
        var boundary = Path.Combine(Root, "workspaces", "default");
        Directory.CreateDirectory(boundary);

        var scope = IndexScopeResolver.Resolve(requested, HasIndex(indexedRoot), boundary);

        Assert.IsNotNull(scope, "请求目录在边界之外时边界应失效；否则索引 scope 配在工作区外的部署上溯永远拿不到作用域");
        Assert.AreEqual(Normalize(indexedRoot), scope!.Value.IndexRoot);
        Assert.AreEqual(
            Path.Combine("Source", "PuddingPlatform"),
            scope.Value.SubDirectoryFilter,
            "子目录过滤仍必须是相对索引根的相对路径");
    }

    /// <summary>
    /// 边界在「它确实是请求目录的祖先」时仍然生效：不得借用边界之上的索引。
    /// 与上一条成对，防止为了修上一条而把边界彻底删掉。
    /// </summary>
    [TestMethod]
    public void Resolve_RequestInsideBoundary_DoesNotBorrowIndexAboveBoundary()
    {
        var above = Path.Combine(Root, "outside");
        var boundary = Path.Combine(above, "workspaces", "default");
        var requested = Path.Combine(boundary, "proj", "src");
        Directory.CreateDirectory(requested);

        var scope = IndexScopeResolver.Resolve(requested, HasIndex(above), boundary);

        Assert.IsNull(scope, "边界之下的请求不得借用边界之上的索引（否则跨界召回）");
    }

    [TestMethod]
    public void Resolve_NoIndexAnywhere_ReturnsNull()
    {
        var deep = Path.Combine(Root, "a", "b", "c");

        var scope = IndexScopeResolver.Resolve(deep, _ => false, Root);

        Assert.IsNull(scope, "无任何有索引祖先时必须返回 null，让调用方保留明确的 not-indexed 语义");
    }

    [TestMethod]
    public void Resolve_TrailingSeparatorAndDotSegments_AreNormalized()
    {
        var project = Path.Combine(Root, "project");
        var requested = Path.Combine(Root, "project", "Source")
            + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar;

        var scope = IndexScopeResolver.Resolve(requested, HasIndex(project), Root);

        Assert.IsNotNull(scope);
        Assert.AreEqual(Normalize(project), scope!.Value.IndexRoot);
        Assert.AreEqual("Source", scope.Value.SubDirectoryFilter);
    }

    [TestMethod]
    public void Resolve_AboveWorkspaceBoundary_ReturnsNull()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pudding-outside-" + Guid.NewGuid().ToString("N"));
        var inner = Path.Combine(Root, "project");

        // 只索引了 inner；请求目录在边界外 ⇒ 不得上溯到 Root/project 去"借"索引。
        var scope = IndexScopeResolver.Resolve(outside, HasIndex(inner), Root);

        Assert.IsNull(scope, "边界外的目录不得借用工作区内的索引，否则会产生假阳性召回");
    }

    [TestMethod]
    public void Resolve_RequestedEqualsBoundary_UsesBoundaryItself()
    {
        var scope = IndexScopeResolver.Resolve(Root, HasIndex(Root), Root);

        Assert.IsNotNull(scope);
        Assert.AreEqual(Normalize(Root), scope!.Value.IndexRoot);
        Assert.IsNull(scope.Value.SubDirectoryFilter);
    }

    [TestMethod]
    public void Resolve_CaseDifference_StillMatchesAncestor()
    {
        var project = Path.Combine(Root, "Project");
        var deep = Path.Combine(Root, "project", "Source");

        var scope = IndexScopeResolver.Resolve(deep, HasIndex(project), Root);

        Assert.IsNotNull(scope, "Windows 路径大小写语义下应命中同一个祖先");
        // IndexRoot 保留的是**请求侧的写法**（GetFullPath 不做盘上大小写归一），
        // 这里必须大小写不敏感地比：引擎用 FullTextIndexPaths.NormalizeCorpusRoot
        // （ToUpperInvariant 后再 SHA256）定位索引目录，所以大小写不同不会导致找不到索引。
        Assert.AreEqual(Normalize(project), scope!.Value.IndexRoot, ignoreCase: true);
    }

    [TestMethod]
    public void Resolve_NullOrEmptyRequested_ReturnsNull()
    {
        Assert.IsNull(IndexScopeResolver.Resolve(null, _ => true, Root));
        Assert.IsNull(IndexScopeResolver.Resolve("   ", _ => true, Root));
    }

    [TestMethod]
    public void Resolve_NullPredicate_ReturnsNull()
    {
        Assert.IsNull(IndexScopeResolver.Resolve(Path.Combine(Root, "x"), null, Root));
    }

    // ---- helpers -----------------------------------------------------------

    private static Func<string, bool> HasIndex(params string[] indexed)
    {
        var set = new HashSet<string>(indexed.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        return candidate => set.Contains(Normalize(candidate));
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
