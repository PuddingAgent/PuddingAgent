using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Search;

namespace PuddingFullTextIndex.Cli.Tests;

/// <summary>
/// A19「scope → 索引目录映射」单一真源化的验收（A1 / A2 / A4）。
/// <para>
/// **四方**（每次断言都要求逐字节相同）：
/// ① 组件公开纯 helper <see cref="FullTextIndexPaths"/>（规则的唯一实现）；
/// ② 引擎 <see cref="IFullTextIndexRootedEngine.ResolveIndexDirectory"/>（经 <c>GetIndexDirectoryPath</c> 委托 ①）；
/// ③ 引擎 <see cref="IFullTextIndexRootedEngine.ProbeDocuments"/> **实际解析到**的目录（用真实 Lucene 索引反证）；
/// ④ 旧镜像金标准 <see cref="ScopeMirrorGolden"/>（A19 之前 CLI 复刻逻辑的逐字冻结副本）。
/// </para>
/// <para>
/// ⚠️ 硬护栏：所有索引根 / 语料根都在 <see cref="Path.GetTempPath"/> 之下（<see cref="CliFixture"/> 构造即断言），
/// 任何用例都不得写生产索引根。
/// </para>
/// </summary>
[TestClass]
public sealed class A19SingleSourceTests
{
    /// <summary>冻结哈希：<c>C:\Corpus\Alpha</c> 的 5 种等价写法（pwsh 独立计算）。</summary>
    private const string FrozenAlphaHash = "6de429b30f1bfcb431bb1e7067b1c1539a8ed10792883161c67155366184556f";

    /// <summary>冻结哈希：<c>C:\语料 根\子目录 一</c>（含空格与中文）。</summary>
    private const string FrozenCjkHash = "3c7545fa1b60342b982c656ddcf0a77379ae33a07d5eef338ca2fe6b9ce89ff6";

    /// <summary>冻结哈希：<c>C:\</c>（<b>不</b>保住盘根 ⇒ 哈希输入是 <c>C:</c>；引擎既有语义）。</summary>
    private const string FrozenDriveRootHash = "82179da1705476f753fdbb9fb58fef251195a42a5d468128266e12228af7e75b";

    /// <summary>
    /// 6 类边界输入（任务书 §3 R4）+ **由 pwsh + BCL 独立算出的冻结哈希**：
    /// ① 普通路径 ② 大小写混合 ③ 尾随 <c>\</c> ④ 尾随 <c>/</c> ⑤ 含 <c>..</c> 相对段 ⑥ 含空格与中文。
    /// </summary>
    private static readonly (string Label, string CorpusRoot, string ExpectedHash)[] FrozenCases =
    {
        ("①普通路径", @"C:\Corpus\Alpha", FrozenAlphaHash),
        ("②大小写混合", @"c:\corpus\alpha", FrozenAlphaHash),
        ("③尾随反斜杠", @"C:\Corpus\Alpha\", FrozenAlphaHash),
        ("④尾随正斜杠", @"C:\Corpus\Alpha/", FrozenAlphaHash),
        ("⑤含 .. 相对段", @"C:\Corpus\Nested\..\Alpha", FrozenAlphaHash),
        ("⑥含空格与中文", @"C:\语料 根\子目录 一", FrozenCjkHash),
    };

    /// <summary>
    /// A1（冻结字面量层）：6 类边界输入的 64 位小写 hex 已由 pwsh 独立算出并冻结；
    /// 金标准 / 组件 helper / 引擎 <c>ResolveIndexDirectory</c> 三者必须都等于
    /// <c>Path.Combine(indexRoot, &lt;冻结 hex&gt;)</c>。
    /// <para>本方法不落盘（用不到探针；探针的四方等价在 <see cref="A1_Golden_Helper_Engine_Resolve_And_Probe_Agree_On_Six_Boundary_Classes"/> 里用真索引反证）。</para>
    /// </summary>
    [TestMethod]
    public void A1_Frozen_Hash_Literals_Match_Golden_Helper_And_Engine()
    {
        using var fixture = new CliFixture();

        var options = new FullTextIndexOptions { IndexRootDirectory = fixture.IndexRoot };
        using var engine = new LuceneSearchEngine(options);
        var rooted = (IFullTextIndexRootedEngine)engine;

        foreach (var (label, corpusRoot, expectedHash) in FrozenCases)
        {
            var expected = Path.Combine(fixture.IndexRoot, expectedHash);

            Assert.AreEqual(
                expected,
                ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, corpusRoot),
                $"旧镜像金标准与冻结哈希不一致（{label}）：{corpusRoot}");
            Assert.AreEqual(
                expected,
                FullTextIndexPaths.ResolveIndexDirectory(fixture.IndexRoot, corpusRoot),
                $"组件 helper 与冻结哈希不一致（{label}）：{corpusRoot}");
            Assert.AreEqual(
                expected,
                rooted.ResolveIndexDirectory(corpusRoot),
                $"引擎 ResolveIndexDirectory 与冻结哈希不一致（{label}）：{corpusRoot}");

            Assert.AreEqual(64, expectedHash.Length, $"冻结值必须是 64 位 hex（{label}）");
            Assert.AreEqual(
                expectedHash,
                Path.GetFileName(expected),
                $"索引目录名必须逐字节等于冻结 hex（{label}）");
        }

        // 盘根语义：C:\ 不保住尾分隔符（与 SupplyScopeNormalizer 刻意不同）—— 冻结下来防"顺手统一"
        Assert.AreEqual(
            Path.Combine(fixture.IndexRoot, FrozenDriveRootHash),
            rooted.ResolveIndexDirectory(@"C:\"),
            "C:\\ 的哈希输入必须是 C:（引擎既有语义）");

        Assert.IsTrue(fixture.IndexRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fixture.IndexRoot.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A1（真实构建层）：6 类边界输入下，四方逐字节相同 ——
    /// 金标准 / 组件 helper / 引擎 <c>ResolveIndexDirectory</c> /
    /// 引擎 <c>ProbeDocuments</c> 解析到的目录（用**两个文档数不同**的真 Lucene 索引反证：
    /// 若某一路径解析到了别的目录，篇数或存在性必然对不上）。
    /// </summary>
    [TestMethod]
    public void A1_Golden_Helper_Engine_Resolve_And_Probe_Agree_On_Six_Boundary_Classes()
    {
        using var fixture = new CliFixture();

        // 真实语料根 A：<temp>\corpus\alpha（2 个可索引文件）
        var corpusA = fixture.CreateSubDirectory("alpha");
        fixture.WriteTo(corpusA, "a.cs", "class A { public int Value => 1; }");
        fixture.WriteTo(corpusA, "b.md", "# Alpha");

        // 真实语料根 B：<temp>\语料 根\子目录 一（5 个可索引文件；空格 + 中文）
        var corpusB = Path.Combine(fixture.Root, "语料 根", "子目录 一");
        Directory.CreateDirectory(corpusB);
        for (var i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(corpusB, "f" + i + ".cs"), "class F" + i + " { }");

        var options = new FullTextIndexOptions { IndexRootDirectory = fixture.IndexRoot };
        using var engine = new LuceneSearchEngine(options);
        var rooted = (IFullTextIndexRootedEngine)engine;

        var buildA = engine.BuildIndexAsync(corpusA).GetAwaiter().GetResult();
        var buildB = engine.BuildIndexAsync(corpusB).GetAwaiter().GetResult();
        Assert.IsTrue(buildA.Success, buildA.Error);
        Assert.IsTrue(buildB.Success, buildB.Error);
        Assert.AreEqual(2, buildA.IndexedFileCount, "语料根 A 必须正好索引 2 个文件");
        Assert.AreEqual(5, buildB.IndexedFileCount, "语料根 B 必须正好索引 5 个文件");

        var inputs = new (string Label, string Path, int ExpectedDocs)[]
        {
            ("①普通路径", corpusA, 2),
            ("②大小写混合", Path.Combine(fixture.Root, "CORPUS", "Alpha"), 2),
            ("③尾随反斜杠", corpusA + "\\", 2),
            ("④尾随正斜杠", corpusA + "/", 2),
            ("⑤含 .. 相对段", Path.Combine(fixture.Root, "corpus", "nested", "..", "alpha"), 2),
            ("⑥含空格与中文", corpusB, 5),
        };

        foreach (var (label, path, expectedDocs) in inputs)
        {
            var golden = ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, path);
            var helper = FullTextIndexPaths.ResolveIndexDirectory(fixture.IndexRoot, path);
            var viaEngine = rooted.ResolveIndexDirectory(path);

            Assert.AreEqual(golden, helper, $"组件 helper 与旧镜像金标准不一致（{label}）：{path}");
            Assert.AreEqual(golden, viaEngine, $"引擎 ResolveIndexDirectory 与旧镜像金标准不一致（{label}）：{path}");
            Assert.IsTrue(
                Directory.Exists(golden),
                $"金标准解析出的目录必须就是引擎真实建出的那个目录（{label}）：{golden}");

            var probe = rooted.ProbeDocuments(path);
            Assert.IsTrue(probe.Exists, $"ProbeDocuments 必须解析到同一个已存在目录（{label}）：{path}");
            Assert.AreEqual(
                (long?)expectedDocs,
                probe.Documents,
                $"ProbeDocuments 解析出的目录不是金标准目录（{label}）：{path}");
        }

        // ②③④⑤ 是同一目录的等价写法 ⇒ 索引根下只应有 ①与⑥ 两个 hash 目录
        var hashDirs = Directory
            .GetDirectories(fixture.IndexRoot)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
        Assert.AreEqual(2, hashDirs.Length, $"索引根下应只有 2 个 hash 目录：{string.Join("; ", hashDirs)}");

        // 负对照（反"探针恒真"）：未建索引的语料根必须报不存在
        var neverBuilt = Path.Combine(fixture.Root, "corpus", "never-built");
        Assert.IsFalse(
            Directory.Exists(ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, neverBuilt)));
        var probeMissing = rooted.ProbeDocuments(neverBuilt);
        Assert.IsFalse(
            probeMissing.Exists,
            "负对照失败：探针对未建索引的语料根报了 Exists=true —— 上面的四方断言将失去检测力");
        Assert.IsNull(probeMissing.Documents, "目录不存在时 Documents 必须是 null（不得伪报 0）");

        Assert.IsTrue(fixture.IndexRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(fixture.IndexRoot.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A2：CLI 打印的 <c>scopeKey</c> / <c>indexDirectory</c> 与**改动前**（旧镜像金标准）逐字相同，
    /// 同时等于组件单一真源与引擎口径；人类可读视图与 <c>--json</c> 视图必须同值。
    /// </summary>
    [TestMethod]
    public void A2_Cli_Printed_ScopeKey_And_IndexDirectory_Are_Byte_For_Byte_The_Frozen_Golden()
    {
        using var fixture = new CliFixture();
        fixture.Write("a.cs", "class A { }");

        var run = fixture.Run("status", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot);
        Assert.AreEqual(0, run.ExitCode, run.StdErr);

        var printedScopeKey = run.Value("scope[0].scopeKey");
        var printedIndexDirectory = run.Value("scope[0].indexDirectory");

        Assert.AreEqual(
            ScopeMirrorGolden.ToScopeKey(ScopeMirrorGolden.NormalizeRoot(fixture.Corpus)),
            printedScopeKey,
            "CLI 打印的 scopeKey 必须与改动前口径逐字相同（本切片未动 ToScopeKey）");

        Assert.AreEqual(
            ScopeMirrorGolden.ResolveIndexDirectory(fixture.IndexRoot, fixture.Corpus),
            printedIndexDirectory,
            "CLI 打印的 indexDirectory 必须与改动前（旧镜像）逐字相同");

        Assert.AreEqual(
            FullTextIndexPaths.ResolveIndexDirectory(fixture.IndexRoot, fixture.Corpus),
            printedIndexDirectory,
            "CLI 打印的 indexDirectory 必须等于组件单一真源");

        var options = new FullTextIndexOptions { IndexRootDirectory = fixture.IndexRoot };
        using var engine = new LuceneSearchEngine(options);
        Assert.AreEqual(
            printedIndexDirectory,
            ((IFullTextIndexRootedEngine)engine).ResolveIndexDirectory(fixture.Corpus),
            "CLI 打印的 indexDirectory 必须等于引擎口径（否则 status 会指向一个引擎不会用的目录）");

        var jsonRun = fixture.Run("status", "--scope", fixture.Corpus, "--index-root", fixture.IndexRoot, "--json");
        Assert.AreEqual(0, jsonRun.ExitCode, jsonRun.StdErr);
        using var json = jsonRun.Json();
        Assert.AreEqual(
            printedIndexDirectory,
            json.RootElement.GetProperty("scopes")[0].GetProperty("indexDirectory").GetString(),
            "JSON 视图与人类可读视图必须同值");
        Assert.AreEqual(
            printedScopeKey,
            json.RootElement.GetProperty("scopes")[0].GetProperty("scopeKey").GetString());

        // 硬护栏：只能落在 %TEMP% 下的临时索引根，绝不指向生产数据根
        Assert.IsTrue(
            printedIndexDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            printedIndexDirectory);
        Assert.IsFalse(printedIndexDirectory.Contains(@"D:\data", StringComparison.OrdinalIgnoreCase), printedIndexDirectory);
    }

    /// <summary>
    /// A4（结构性）：**生产工程** <c>Source/PuddingFullTextIndex.Cli</c> 内不得再出现命名哈希的复刻
    /// （<c>SHA256.HashData</c> / <c>ToUpperInvariant</c> / <c>Convert.ToHexStringLower</c>）。
    /// <para>
    /// 含两项**仪器自检**（"搜不到"必须能区分于"没扫到"）：
    /// ① 扫描确实覆盖到生产文件（含 <c>SupplyCli.cs</c> / <c>SupplyScopeMirror.cs</c>）；
    /// ② 金标准副本确实仍在**测试工程**（<see cref="ScopeMirrorGolden"/>），否则 A1/A2 会退化为同义反复。
    /// </para>
    /// </summary>
    [TestMethod]
    public void A4_Cli_Production_Project_Has_No_Replica_Of_The_Naming_Hash()
    {
        var cliProjectDirectory = RepoLayout.SourceProjectDirectory("PuddingFullTextIndex.Cli");
        Assert.IsTrue(Directory.Exists(cliProjectDirectory), cliProjectDirectory);

        var files = Directory
            .GetFiles(cliProjectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(f => !RepoLayout.IsBuildOutput(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        // 仪器自检 ①：必须真的扫到生产文件
        Assert.IsGreaterThanOrEqualTo(4, files.Length, $"CLI 生产源文件数异常：{string.Join("; ", files)}");
        Assert.IsTrue(
            files.Any(f => Path.GetFileName(f) == "SupplyCli.cs"),
            $"扫描必须覆盖 SupplyCli.cs：{string.Join("; ", files)}");
        Assert.IsTrue(
            files.Any(f => Path.GetFileName(f) == "SupplyScopeMirror.cs"),
            $"扫描必须覆盖 SupplyScopeMirror.cs：{string.Join("; ", files)}");

        string[] forbidden = { "SHA256.HashData", "ToUpperInvariant", "Convert.ToHexStringLower" };

        foreach (var token in forbidden)
        {
            var hits = new List<string>();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                        hits.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }

            Assert.IsEmpty(
                hits,
                $"CLI 生产工程内不得再复刻命名哈希（{token}）—— 应改调 FullTextIndexPaths：\n{string.Join("\n", hits)}");
        }

        // 仪器自检 ②：金标准副本（豁免对象）确实还在测试工程里
        var goldenPath = Path.Combine(
            RepoLayout.SourceProjectDirectory("PuddingFullTextIndex.Cli.Tests"),
            "ScopeMirrorGolden.cs");
        Assert.IsTrue(File.Exists(goldenPath), $"金标准副本必须存在（否则 A1/A2 无独立 oracle）：{goldenPath}");
        var goldenText = File.ReadAllText(goldenPath);
        foreach (var token in forbidden)
        {
            StringAssert.Contains(
                goldenText,
                token,
                $"金标准副本必须仍然复刻旧口径（{token}）—— 否则 A1/A2 退化成「自己和自己比」：{goldenPath}");
        }
    }

    /// <summary>仓库布局定位（从测试程序集向上找含 <c>PuddingAgentNetwork.slnx</c> 的目录；找不到即失败，不静默跳过）。</summary>
    private static class RepoLayout
    {
        internal const string RootMarker = "PuddingAgentNetwork.slnx";

        internal static string Root { get; } = FindRoot();

        internal static string SourceProjectDirectory(string project) =>
            Path.Combine(Root, "Source", project);

        internal static bool IsBuildOutput(string path) =>
            path.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

        private static string FindRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, RootMarker)))
                    return directory.FullName;

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"从 {AppContext.BaseDirectory} 向上找不到仓库根（缺 {RootMarker}）");
        }
    }
}
