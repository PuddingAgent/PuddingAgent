using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;

namespace PuddingFullTextIndexTests;

/// <summary>
/// <b>S4 边界与安全断言</b>（<c>Docs/Conventions/组件化交付规程.md</c> §3 的 S4 判据）：把
/// 「<c>PuddingFullTextIndex</c> 是叶子级别、<b>无宿主</b>的独立组件」从注释里的<b>承诺</b>
/// 变成<b>能失败的机械检查</b>。
/// <para>
/// 判据来源（S4 原文）：新组件内检索调用方程序集名 ⇒ <b>0 命中</b>；csproj 无禁用包；
/// <c>ProjectReference</c> 清单符合预期；依赖方向由<b>编译期</b>强制（组件 → 只允许
/// <c>PuddingPathFiltering</c>）。
/// </para>
/// <para>
/// 本文件是 S4 片（任务书 <c>temp/s4-boundary-assertions-task.md</c>）的交付物，
/// 逐条结论与变异证据见 <c>temp/s4-report.md</c>。<b>零生产代码改动</b>：所有断言都是只读扫描；
/// 本文件<b>不</b>创建任何索引目录，也不触碰生产索引根 <c>D:\data\fulltext-index</c>。
/// </para>
/// <para>
/// 每条断言都遵守两条仪器纪律：① <b>探测器自检</b>（<see cref="Boundary_Detector_Flags_Forbidden_Names"/>
/// 与各处 control 断言）—— 绿色断言若探测器坏了就毫无意义；② <b>真空防护</b> —— 先证明扫描面非空
/// （组件、Maintenance、依赖闭包、真源文件都在），否则「0 违规」可能只是「什么都没扫到」。
/// </para>
/// </summary>
[TestClass]
public sealed class ComponentBoundaryTests
{
    // ─────────────────────────────────────────────────────────────────────
    // 单一真源：禁用程序集清单（A3 与 A5 共用同一份，禁止写两份 —— 任务书 §3/A5）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>上层程序集名：组件<b>不得</b>经 <c>ProjectReference</c> / <c>InternalsVisibleTo</c> / 源码引用它们。</summary>
    private static readonly string[] ForbiddenAssemblyNames =
    [
        "PuddingHost",
        "PuddingRuntime",
        "PuddingAgent",
        "PuddingCore",
        "PuddingCodeIndex",
        "PuddingCodeIntelligence",
        "PuddingController",
        "PuddingDesktop",
    ];

    /// <summary>刻意隔离的重依赖的程序集名前缀（重依赖隔离的实证判据）。</summary>
    private static readonly string[] ForbiddenAssemblyPrefixes =
    [
        "Microsoft.CodeAnalysis",
        "Microsoft.Build",
    ];

    private const string ComponentAssemblyName = "PuddingFullTextIndex";
    private const string LeafDependencyAssemblyName = "PuddingPathFiltering";
    private const string ComponentProjectFileName = "PuddingFullTextIndex.csproj";
    private const string TestProjectFileName = "PuddingFullTextIndexTests.csproj";
    private const string ComponentRelativeDirectory = @"Source\PuddingFullTextIndex";
    private const string TestProjectRelativeDirectory = @"Source\PuddingFullTextIndexTests";
    private const string EngineSourceRelativePath = @"Infrastructure\Search\LuceneSearchEngine.cs";
    private const string CheckpointSourceRelativePath = @"Infrastructure\Maintenance\MaintenanceCheckpoint.cs";
    private const string SupplyOptionsRelativePath = @"SupplyCoordinatorOptions.cs";
    private const string MaintenanceRelativeDirectory = @"Infrastructure\Maintenance";
    private const string PathNoiseRulesLeafRelativePath = @"Source\PuddingPathFiltering\PathNoiseRules.cs";

    /// <summary>A1 冻结值：组件<b>恰好</b>一条 <c>ProjectReference</c>（ADR-089 U4-4 D4）。</summary>
    private static readonly string[] ExpectedProjectReferences =
    [
        @"..\PuddingPathFiltering\PuddingPathFiltering.csproj",
    ];

    /// <summary>A2 冻结值：组件<b>恰好</b>六个 NuGet 包（多一个或少一个都红）。</summary>
    private static readonly string[] ExpectedPackageReferences =
    [
        "Lucene.Net",
        "Lucene.Net.Analysis.Common",
        "Lucene.Net.QueryParser",
        "jieba.NET",
        "Newtonsoft.Json",
        "System.Drawing.Common",
    ];

    /// <summary>
    /// A4 冻结值：测试工程<b>恰好</b>引用组件本身（否则 S2/S3 的「无宿主」性质失效）。
    /// </summary>
    private static readonly string[] ExpectedTestProjectReferences =
    [
        @"..\PuddingFullTextIndex\PuddingFullTextIndex.csproj",
    ];

    /// <summary>
    /// A3 冻结的「开放面」：磁盘现状（S4 实测 2026-09-26）。
    /// <para>
    /// ⚠️ <b>S4 实测发现</b>：<c>PuddingMemoryEngine</c> / <c>PuddingMemoryEngineTests</c> 是组件的<b>消费者</b>
    /// （上层），按规程 §3.2「为新组件反向开放 InternalsVisibleTo 给上层程序集 = 实质反向依赖」属于<b>可疑面</b>；
    /// 但它们<b>不在</b>任务书 A3 的禁用清单内，故本断言<b>保留</b>现状并冻结集合 ——
    /// 任何<b>新增</b>的开放目标都会被拦下（详见 <c>temp/s4-report.md</c>「与任务书不符」节）。
    /// </para>
    /// </summary>
    private static readonly string[] ObservedInternalsVisibleTo =
    [
        "PuddingFullTextIndexTests",
        "PuddingMemoryEngine",
        "PuddingMemoryEngineTests",
    ];

    /// <summary>
    /// A8 冻结值：全组件内「触发 SHA-256 类型名」的<b>文件集合</b>（序数排序）。
    /// <para>
    /// ⚠️ <b>S4 实测修正</b>：任务书 §3/A8 写的是「只出现在这两个文件」（<c>FullTextIndexPaths</c> +
    /// <c>FullTextPolicyFingerprint</c>），磁盘实测为 <b>4</b> 个：<c>Supply</c> 层的
    /// <c>FileSupplyLease</c> / <c>SupplyIndexDirectoryLayout</c> 各持一份「供给层命名用」的
    /// <c>SHA256.HashData</c>（其类注释自述「仅用于供给层的命名」）。按任务书「先如实报告、不得为变绿改生产代码」，
    /// 本断言把集合冻结为<b>实测真态</b>并登记该差异。
    /// </para>
    /// </summary>
    private static readonly string[] Sha256SingleSourceFiles =
    [
        @"Infrastructure\FullTextIndexPaths.cs",
        @"Infrastructure\FullTextPolicyFingerprint.cs",
        @"Infrastructure\Supply\FileSupplyLease.cs",
        @"Infrastructure\Supply\SupplyIndexDirectoryLayout.cs",
    ];

    /// <summary>
    /// A10 冻结值：maintenance 路径<b>不得</b>出现的 API / 类型名（真名从磁盘查出，非任务书举例）。
    /// </summary>
    private static readonly string[] MaintenanceForbiddenTokens =
    [
        "BuildIndexAsync",
        "RemoveIndex",
        "FullTextIndexSupplyCoordinator",
        "IFullTextIndexSupplyCoordinator",
        "StagedFullTextIndexBuilder",
    ];

    /// <summary>
    /// A10 的 <c>OpenMode.CREATE</c> 判定（<b>不含</b> <c>CREATE_OR_APPEND</c>）。
    /// <c>\bCREATE\b</c> 在 .NET 正则里已足够：<c>_</c> 是词字符 ⇒ <c>CREATE_OR_APPEND</c> 的 <c>CREATE</c>
    /// 之后<b>没有</b>词边界。同时必须<b>大小写敏感</b>：<c>IndexSizeReport.Create(</c> 这类工厂方法名不得被误判（防假红）。
    /// </summary>
    private static readonly Regex CreateWithoutAppendPattern = new(@"\bCREATE\b", RegexOptions.Compiled);

    /// <summary>A7：<c>PathNoiseRules</c> 的唯一真源是 <c>PuddingPathFiltering</c>，组件内不得复刻<b>类型定义</b>。</summary>
    private static readonly Regex TypeDefinitionPattern =
        new(@"\b(class|record|struct|interface)\s+PathNoiseRules\b", RegexOptions.Compiled);

    /// <summary>A9a 冻结值：<c>IFullTextSearchEngine</c> 的公开实例成员（<c>名字(参数个数)</c>，序数排序）。</summary>
    private static readonly string[] FrozenSearchEngineMembers =
    [
        "BuildIndexAsync(3)",
        "HasIndex(1)",
        "RemoveIndex(1)",
        "SearchAsync(7)",
    ];

    /// <summary>A9b 冻结值（集合口径，序数排序）：<c>.last_indexed</c> 落盘 stamp 的顶层键。</summary>
    private static readonly string[] FrozenStampKeySet = ["p", "t"];

    /// <summary>A9b 冻结值（顺序口径）：写入顺序即协议 —— <c>{"t":…,"p":…}</c>。</summary>
    private static readonly string[] FrozenStampKeyOrder = ["t", "p"];

    private static readonly Regex IsoUtcStampShape =
        new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", RegexOptions.Compiled);

    private static readonly Regex PatternHashShape = new("^[0-9a-f]{12}$", RegexOptions.Compiled);

    /// <summary>
    /// <b>探测器自检</b>：绿色边界断言若探测器坏了就毫无意义（「0 违规」可能只是「过滤器坏了」）。
    /// </summary>
    [TestMethod]
    public void Boundary_Detector_Flags_Forbidden_Names()
    {
        var flagged = FindViolations(
        [
            "PuddingFullTextIndex",
            "PuddingFullTextIndexTests",
            "PuddingPathFiltering",
            "MSTest.TestFramework",
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.Build.Locator",
            "PuddingCodeIntelligence",
            "PuddingRuntime.Services",
            "PuddingHost",
            "PuddingAgent",
        ]);

        CollectionAssert.AreEquivalent(
            new[]
            {
                "Microsoft.Build.Locator",
                "Microsoft.CodeAnalysis.CSharp",
                "PuddingCodeIntelligence",
                "PuddingHost",
                "PuddingRuntime.Services",
                "PuddingAgent",
            },
            flagged.ToArray(),
            "the boundary detector must flag exactly the forbidden assembly names, and nothing else");

        Assert.IsEmpty(
            FindViolations(["PuddingFullTextIndex", "PuddingFullTextIndexTests", "MSTest.TestFramework"]),
            "the detector must not flag allowed assemblies");
    }

    /// <summary>A1：组件 csproj 的 <c>ProjectReference</c> 清单符合预期（先剥 XML 注释）。</summary>
    [TestMethod]
    public void A1_Component_Project_References_Only_The_Path_Filtering_Leaf()
    {
        var text = StripXmlComments(File.ReadAllText(Path.Combine(ComponentDirectory(), ComponentProjectFileName)));
        var references = ProjectReferenceTargets(text).Select(NormalizeSeparators).ToArray();

        // 真空防护：扫描面必须真的含 ProjectReference
        Assert.IsTrue(
            text.Contains("<ProjectReference", StringComparison.Ordinal),
            "control: 组件 csproj 必须真的含 ProjectReference（否则下面的清单断言是真空的）");

        CollectionAssert.AreEqual(
            ExpectedProjectReferences.Select(NormalizeSeparators).ToArray(),
            references,
            "组件 csproj 的 ProjectReference 清单被改动（实测 = "
            + string.Join(", ", references)
            + "）；组件只允许「PuddingPathFiltering」这一条叶子依赖");
    }

    /// <summary>A2：组件 csproj 的 <c>PackageReference</c> 名集合<b>恰好</b>等于冻结的 6 个。</summary>
    [TestMethod]
    public void A2_Component_Package_References_Match_The_Frozen_Set()
    {
        var text = StripXmlComments(File.ReadAllText(Path.Combine(ComponentDirectory(), ComponentProjectFileName)));

        var actual = PackageReferenceNames(text)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var expected = ExpectedPackageReferences
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.AreEqual(6, expected.Length, "control: 冻结清单本身必须是 6 项");

        var extra = actual.Except(expected, StringComparer.Ordinal).ToArray();
        var missing = expected.Except(actual, StringComparer.Ordinal).ToArray();

        Assert.IsEmpty(
            extra,
            "组件 csproj 出现**多出**的包引用（S4 冻结面变更，必须显式裁定）: " + string.Join(", ", extra));
        Assert.IsEmpty(missing, "组件 csproj **缺失**预期的包引用: " + string.Join(", ", missing));
        CollectionAssert.AreEqual(expected, actual, "组件 csproj 的 PackageReference 清单必须与冻结值逐项一致");
    }

    /// <summary>A3：组件 csproj 无禁用引用目标、无向上层反向开放的 <c>InternalsVisibleTo</c>。</summary>
    [TestMethod]
    public void A3_Component_Project_Has_No_Forbidden_References()
    {
        var text = StripXmlComments(File.ReadAllText(Path.Combine(ComponentDirectory(), ComponentProjectFileName)));

        var forbiddenReferences = ProjectReferenceTargets(text)
            .Select(target => Path.GetFileNameWithoutExtension(NormalizeSeparators(target)))
            .Where(IsForbiddenAssemblyName)
            .ToArray();
        Assert.IsEmpty(
            forbiddenReferences,
            "组件 csproj 不得引用上层程序集: " + string.Join(", ", forbiddenReferences));

        var internalsVisibleTo = InternalsVisibleToTargets(text)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // 真空防护
        Assert.IsTrue(
            internalsVisibleTo.Length > 0,
            "control: 组件 csproj 必须真的含 InternalsVisibleTo（否则下面的断言是真空的）");

        var forbiddenInternals = internalsVisibleTo.Where(IsForbiddenAssemblyName).ToArray();
        Assert.IsEmpty(
            forbiddenInternals,
            "组件不得向上层程序集反向开放 InternalsVisibleTo（规程 §3.2）: " + string.Join(", ", forbiddenInternals));

        var extra = internalsVisibleTo.Except(ObservedInternalsVisibleTo, StringComparer.Ordinal).ToArray();
        var missing = ObservedInternalsVisibleTo.Except(internalsVisibleTo, StringComparer.Ordinal).ToArray();
        Assert.IsEmpty(
            extra,
            "组件新增了 InternalsVisibleTo 开放目标（S4 冻结的开放面 = "
            + string.Join(", ", ObservedInternalsVisibleTo)
            + "），必须显式裁定: "
            + string.Join(", ", extra));
        Assert.IsEmpty(missing, "组件开放面**消失**（冻结值未同步或被人为收窄）: " + string.Join(", ", missing));
    }

    /// <summary>A4：测试工程只引用组件（否则 S2/S3 的「无宿主」性质失效）。</summary>
    [TestMethod]
    public void A4_Test_Project_References_Only_The_Component()
    {
        var csproj = Path.Combine(RepositoryRoot(), TestProjectRelativeDirectory, TestProjectFileName);
        Assert.IsTrue(File.Exists(csproj), $"expected the component test project at {csproj}");

        var text = StripXmlComments(File.ReadAllText(csproj));
        var references = ProjectReferenceTargets(text).Select(NormalizeSeparators).ToArray();

        Assert.AreEqual(1, references.Length, "测试工程必须恰好一条 ProjectReference，实测: " + string.Join(", ", references));
        CollectionAssert.AreEqual(
            ExpectedTestProjectReferences.Select(NormalizeSeparators).ToArray(),
            references,
            "测试工程只允许引用组件本身（引上层 ⇒ S3 的「无宿主验证」不再有意义）");
    }

    /// <summary>
    /// A5：测试进程未加载禁用程序集。三段式（模板继承）：force-load 元数据引用
    /// （防 CLR 惰性加载让 AppDomain-only 检查<b>永远绿</b>）+ <c>*.deps.json</c> 声明闭包交集
    /// （防 <c>bin/</c> 陈旧残留造成假阳性）+ 真空防护。
    /// </summary>
    [TestMethod]
    public void A5_Test_Process_Must_Not_Load_Forbidden_Assemblies()
    {
        var testAssembly = typeof(ComponentBoundaryTests).Assembly;
        Assert.AreEqual(
            "PuddingFullTextIndexTests",
            testAssembly.GetName().Name,
            "boundary assertions must run inside the component test assembly");

        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        var referenced = testAssembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToArray();

        var forceLoaded = new List<string>();
        foreach (var name in referenced)
        {
            try
            {
                forceLoaded.Add(Assembly.Load(name)!.GetName().Name!);
            }
            catch (Exception)
            {
                // Unresolvable references are irrelevant here; the assertion is about *forbidden* names.
            }
        }

        // Reachable = 探测路径上的 dll ∩ 声明闭包（后者来自 SDK 生成的 deps.json）
        var declared = ReadDependencyClosure();
        var reachable = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Where(name => declared.Contains(name, StringComparer.Ordinal))
            .ToArray();

        var observed = loaded
            .Concat(referenced)
            .Concat(forceLoaded)
            .Concat(reachable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(
            ComponentAssemblyName,
            observed,
            "control: 组件本身必须在本进程闭包内，否则本断言是真空的");
        Assert.Contains(
            ComponentAssemblyName,
            reachable,
            "control: 组件必须能经探测路径（bin + deps.json 闭包）到达");
        Assert.Contains(
            LeafDependencyAssemblyName,
            observed,
            "control: 组件的唯一叶子依赖也必须在闭包内");

        var violations = FindViolations(observed);
        Assert.IsEmpty(
            violations,
            "本测试进程不得加载 Roslyn/MSBuild 或上层程序集，实测命中: " + string.Join(", ", violations));
    }

    /// <summary>
    /// A5（确定性引信）：测试工程的 <c>*.deps.json</c> <b>声明闭包</b>不得提及禁用程序集。
    /// 与「已加载程序集」互补 —— 它由 SDK 从引用闭包生成，一条游离的 <c>ProjectReference</c> 无法绕过它。
    /// </summary>
    [TestMethod]
    public void A5_Test_Dependency_Closure_Must_Not_Contain_Forbidden_Assemblies()
    {
        var closure = ReadDependencyClosure();

        Assert.Contains(
            ComponentAssemblyName,
            closure,
            "control: 声明闭包必须含组件本身，否则本断言是真空的");
        Assert.Contains(
            LeafDependencyAssemblyName,
            closure,
            "control: 声明闭包必须含组件的叶子依赖");

        var violations = FindViolations(closure);
        Assert.IsEmpty(
            violations,
            "PuddingFullTextIndexTests 的声明依赖闭包不得含 Roslyn/MSBuild 或上层程序集，实测命中: "
            + string.Join(", ", violations));
    }

    /// <summary>
    /// A6：组件源码内不出现调用方（上层）程序集名。
    /// <para>
    /// ⚠️ <b>S4 实测口径修正</b>：只剥 <b>注释</b>、<b>不剥字符串字面量</b>。理由有二：① 磁盘实测字符串内
    /// 0 命中（见报告），不剥不会误伤现状；② 任务书 §4/M1 的<b>后备变异形态</b>恰恰是把禁用名写进
    /// <b>字符串字面量</b>（<c>const string ... = "PuddingHost";</c>）—— 若剥掉字符串，该变异就<b>无法取红</b>。
    /// 保留字符串扫面 = 更强、且可被变异推翻的断言。
    /// </para>
    /// </summary>
    [TestMethod]
    public void A6_Component_Sources_Must_Not_Name_Caller_Assemblies()
    {
        var sources = EnumerateComponentSources();
        Assert.IsTrue(sources.Length > 0, "control: 组件源码扫描面不得为空");

        // 真空防护：扫描面必须真的覆盖到组件的实现文件
        Assert.IsTrue(
            sources.Contains(EngineSourceRelativePath, StringComparer.Ordinal),
            "control: 扫描面必须包含 " + EngineSourceRelativePath);

        // 仪器自检：探测器必须能命中禁用名
        Assert.IsTrue(
            FindLineHits(SanitizeSource("var x = PuddingHost.Marker;", removeStringLiterals: false), ForbiddenAssemblyNames).Count > 0,
            "control: 禁用名探测器必须能命中");

        // 仪器自检：注释剥离必须真的生效（磁盘上确有一处 doc-comment 提及 PuddingHost）
        var supplyOptionsPath = Path.Combine(ComponentDirectory(), SupplyOptionsRelativePath);
        Assert.IsTrue(File.Exists(supplyOptionsPath), $"expected {supplyOptionsPath}");
        var rawSupplyOptions = File.ReadAllText(supplyOptionsPath);
        Assert.IsTrue(
            rawSupplyOptions.Contains("PuddingHost", StringComparison.Ordinal),
            "control: 该文件确实在**注释**里提及 PuddingHost（否则下面的剥离自检是真空的）");
        Assert.IsFalse(
            SanitizeSource(rawSupplyOptions, removeStringLiterals: false).Contains("PuddingHost", StringComparison.Ordinal),
            "control: 剥注释必须能去掉 doc-comment 里的 PuddingHost");

        var offenders = new List<string>();
        foreach (var relative in sources)
        {
            var text = SanitizeSource(ReadComponentSource(relative), removeStringLiterals: false);
            foreach (var (line, token) in FindLineHits(text, ForbiddenAssemblyNames))
                offenders.Add($"{relative}:{line}: {token}");
        }

        Assert.IsEmpty(
            offenders,
            "组件源码不得出现调用方（上层）程序集名（S4 判据：0 命中），实测: " + string.Join(" | ", offenders));
    }

    /// <summary>A7：组件内不得复刻 <c>PathNoiseRules</c> 的<b>类型定义</b>（唯一真源在 PuddingPathFiltering）。</summary>
    [TestMethod]
    public void A7_Component_Does_Not_Redefine_PathNoiseRules()
    {
        var sources = EnumerateComponentSources();

        // 仪器自检：该正则必须能在真源文件上命中（否则「0 命中」可能只是正则坏了）
        var leafText = File.ReadAllText(Path.Combine(RepositoryRoot(), PathNoiseRulesLeafRelativePath));
        Assert.IsTrue(
            TypeDefinitionPattern.IsMatch(leafText),
            "control: 类型定义探测器必须能在 PuddingPathFiltering/PathNoiseRules.cs 上命中");

        var definitions = new List<string>();
        var usageFiles = 0;
        foreach (var relative in sources)
        {
            var text = SanitizeSource(ReadComponentSource(relative), removeStringLiterals: false);
            foreach (Match match in TypeDefinitionPattern.Matches(text))
                definitions.Add($"{relative}:{LineNumberOf(text, match.Index)}: {match.Value}");

            if (text.Contains("PathNoiseRules.", StringComparison.Ordinal))
                usageFiles++;
        }

        // 真空防护：组件确实在**使用**真源（只允许使用、不允许定义）
        Assert.IsTrue(usageFiles > 0, "control: 组件必须确实在使用 PathNoiseRules（否则本断言是真空的）");

        Assert.IsEmpty(
            definitions,
            "组件内不得重复定义 PathNoiseRules（唯一真源在 PuddingPathFiltering），实测: "
            + string.Join(" | ", definitions));
    }

    /// <summary>A8：SHA-256 的类型名只出现在冻结的单一真源文件集合内。</summary>
    [TestMethod]
    public void A8_Sha256_Usage_Is_Limited_To_The_Frozen_Source_Files()
    {
        var sources = EnumerateComponentSources();

        // 仪器自检：磁盘上确有一处「只在注释里提及 SHA256」的文件 ⇒ 剥注释后必须不再命中
        var checkpointPath = Path.Combine(ComponentDirectory(), CheckpointSourceRelativePath);
        Assert.IsTrue(File.Exists(checkpointPath), $"expected {checkpointPath}");
        var rawCheckpoint = File.ReadAllText(checkpointPath);
        Assert.IsTrue(
            rawCheckpoint.Contains("SHA256", StringComparison.Ordinal),
            "control: 该文件确实在**注释**里提及 SHA256");
        Assert.IsFalse(
            SanitizeSource(rawCheckpoint, removeStringLiterals: false).Contains("SHA256", StringComparison.Ordinal),
            "control: 剥注释后该文件的 SHA256 必须消失（否则扫描器把注释也算进来了）");

        var hits = sources
            .Where(relative => SanitizeSource(ReadComponentSource(relative), removeStringLiterals: false)
                .Contains("SHA256", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.IsTrue(hits.Length > 0, "control: SHA-256 扫描面必须能命中至少一个文件");

        var extra = hits.Except(Sha256SingleSourceFiles, StringComparer.Ordinal).ToArray();
        var missing = Sha256SingleSourceFiles.Except(hits, StringComparer.Ordinal).ToArray();

        Assert.IsEmpty(
            extra,
            "SHA-256 的类型名出现在**额外**文件（单一真源分裂风险，S4 判据）: " + string.Join(", ", extra));
        Assert.IsEmpty(missing, "预期的 SHA-256 真源文件未命中（真源被人为搬走？）: " + string.Join(", ", missing));
        CollectionAssert.AreEquivalent(
            Sha256SingleSourceFiles,
            hits,
            "SHA-256 命中文件集合必须恰好等于冻结集合");
    }

    /// <summary>A9a：<c>IFullTextSearchEngine</c> 的公开契约（名字 + 参数个数）冻结。</summary>
    [TestMethod]
    public void A9a_Search_Engine_Contract_Is_Frozen()
    {
        var members = typeof(IFullTextSearchEngine)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(member => member.MemberType == MemberTypes.Method || member.MemberType == MemberTypes.Property)
            .Select(member => member switch
            {
                MethodInfo method => $"{method.Name}({method.GetParameters().Length})",
                PropertyInfo property => property.Name,
                _ => member.Name,
            })
            .Where(descriptor =>
                !descriptor.StartsWith("get_", StringComparison.Ordinal)
                && !descriptor.StartsWith("set_", StringComparison.Ordinal))
            .OrderBy(descriptor => descriptor, StringComparer.Ordinal)
            .ToArray();

        Assert.IsTrue(members.Length > 0, "control: 反射必须取到 IFullTextSearchEngine 的公开成员");

        CollectionAssert.AreEquivalent(
            FrozenSearchEngineMembers,
            members,
            "IFullTextSearchEngine 的公开实例成员被改动（增 / 删 / 改参数个数都会红）；实测 = "
            + string.Join(", ", members));

        Assert.AreEqual(
            FrozenSearchEngineMembers.Length,
            members.Length,
            "成员数必须恰好等于冻结值");
    }

    /// <summary>
    /// A9b：<c>.last_indexed</c> 落盘 stamp 仍<b>只有</b> <c>t</c> 与 <c>p</c> 两个键，且 <c>t</c> 是
    /// UTC ISO 字符串（形如 <c>2026-09-26T12:34:56Z</c>）。
    /// <para>
    /// 两段证据：① <b>源码侧</b> —— 从磁盘切出引擎的写 / 读方法体，断言写入的匿名对象成员恰为 <c>{t,p}</c>、
    /// 读回的属性名恰为 <c>{t,p}</c>；② <b>运行时形状</b> —— 用生产同款序列化器（System.Text.Json）序列化一个
    /// 已知 stamp，断言顶层键集合与 <c>t</c> 的 ISO 形状。端到端往返（真实引擎写盘 + 引擎读回）由
    /// <c>LastIndexedStampRoundTripTests</c> 负责，二者互补：本条在「引擎没跑起来」时也能变红。
    /// </para>
    /// </summary>
    [TestMethod]
    public void A9b_LastIndexed_Stamp_Protocol_Is_Frozen_To_Two_Keys()
    {
        var engineSource = SanitizeSource(ReadComponentSource(EngineSourceRelativePath), removeStringLiterals: false);

        var writer = SliceMethod(engineSource, "private async Task WriteLastIndexedAsync(");
        var reader = SliceMethod(engineSource, "private async Task<LastIndexedStamp?> ReadLastIndexedAsync(");
        Assert.IsTrue(writer.Length > 0, "control: 必须能从磁盘切出 .last_indexed 的写入方法");
        Assert.IsTrue(reader.Length > 0, "control: 必须能从磁盘切出 .last_indexed 的读取方法");

        var serializedObject = Regex.Match(writer, @"JsonSerializer\.Serialize\(new\s*\{([^}]*)\}\)");
        Assert.IsTrue(serializedObject.Success, "control: 写入方法里必须存在 `JsonSerializer.Serialize(new { ... })`");

        var writtenKeys = serializedObject.Groups[1].Value
            .Split(',')
            .Select(part => part.Split('=')[0].Trim())
            .Where(name => name.Length > 0)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            FrozenStampKeySet,
            writtenKeys,
            "`.last_indexed` 写入必须恰为 {t, p}，实测 = {" + string.Join(", ", writtenKeys) + "}");

        var readKeys = Regex.Matches(reader, @"GetProperty\(""([^""]+)""\)")
            .Select(match => match.Groups[1].Value)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            FrozenStampKeySet,
            readKeys,
            "`.last_indexed` 读取必须恰为 {t, p}，实测 = {" + string.Join(", ", readKeys) + "}");

        // 运行时形状：与生产同一序列化器 + 同一指纹真源
        var knownUtc = new DateTime(2026, 9, 26, 12, 34, 56, DateTimeKind.Utc);
        var fingerprint = FullTextPolicyFingerprint.ComputePatternFingerprint("*.md");
        var json = JsonSerializer.Serialize(new { t = knownUtc, p = fingerprint });

        using var document = JsonDocument.Parse(json);
        CollectionAssert.AreEqual(
            FrozenStampKeyOrder,
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray(),
            "运行时 stamp JSON 的顶层键必须恰为 {t, p}（顺序即协议）");
        Assert.AreEqual(
            "2026-09-26T12:34:56Z",
            document.RootElement.GetProperty("t").GetString(),
            "t 必须是扫描开始时刻的 UTC ISO 字符串（带 Z）");
        StringAssert.Matches(
            document.RootElement.GetProperty("t").GetString()!,
            IsoUtcStampShape,
            "t 的形状必须是 ISO-8601 UTC（否则『只有两个键』可能被两个空对象满足）");
        Assert.AreEqual(
            fingerprint,
            document.RootElement.GetProperty("p").GetString(),
            "p 必须来自单一真源 helper");
        StringAssert.Matches(
            document.RootElement.GetProperty("p").GetString()!,
            PatternHashShape,
            "p 必须是 12 位小写 hex");
    }

    /// <summary>
    /// A10：maintenance 源码不得走<b>全量重建</b> / <b>手动供给</b> / <c>OpenMode.CREATE</c> 路径。
    /// 对照：同一扫描器在组件其余源码上<b>必须</b>能命中这些 token（否则「0 命中」可能只是路径写错了）。
    /// </summary>
    [TestMethod]
    public void A10_Maintenance_Sources_Use_Neither_Rebuild_Nor_Supply_Nor_Create_Paths()
    {
        // 仪器自检：CREATE_OR_APPEND 不得被误判（假红防线）；裸 CREATE 必须命中
        Assert.IsFalse(
            CreateWithoutAppendPattern.IsMatch("var mode = OpenMode.CREATE_OR_APPEND;"),
            "control: CREATE_OR_APPEND 不得命中 \\bCREATE\\b");
        Assert.IsTrue(
            CreateWithoutAppendPattern.IsMatch("var mode = OpenMode.CREATE;"),
            "control: 裸 CREATE 必须命中 \\bCREATE\\b");

        var maintenancePaths = EnumerateMaintenanceSourcePaths();
        var componentPaths = EnumerateComponentSourcePaths();

        Assert.IsTrue(
            maintenancePaths.Length >= 10,
            $"control: Maintenance 扫描面必须非空（实测 {maintenancePaths.Length} 个 .cs）");

        var maintenanceText = string.Join('\n', maintenancePaths.Select(File.ReadAllText));
        var componentText = string.Join('\n', componentPaths.Select(File.ReadAllText));

        // 正对照（探针落在真实文件上）：maintenance 确实含 OpenMode.CREATE_OR_APPEND
        Assert.IsTrue(
            maintenanceText.Contains("OpenMode.CREATE_OR_APPEND", StringComparison.Ordinal),
            "control: maintenance 必须真的含 OpenMode.CREATE_OR_APPEND（证明扫描面读到了该文件）");

        var offenders = new List<string>();
        foreach (var path in maintenancePaths)
        {
            var label = Path.GetRelativePath(ComponentDirectory(), path);
            var text = SanitizeSource(File.ReadAllText(path), removeStringLiterals: false);

            foreach (var token in MaintenanceForbiddenTokens)
                foreach (var (line, _) in FindLineHits(text, [token]))
                    offenders.Add($"{label}:{line}: {token}");

            foreach (Match match in CreateWithoutAppendPattern.Matches(text))
                offenders.Add($"{label}:{LineNumberOf(text, match.Index)}: OpenMode.CREATE");
        }

        Assert.IsEmpty(
            offenders,
            "maintenance 路径不得走全量重建（BuildIndexAsync）/ 删索引（RemoveIndex）/ 手动供给（协调器类型名）"
            + " / OpenMode.CREATE，实测: "
            + string.Join(" | ", offenders));

        // 正对照：同一扫描器在组件其余源码上必须能命中每个 token
        foreach (var token in MaintenanceForbiddenTokens)
        {
            Assert.IsTrue(
                componentText.Contains(token, StringComparison.Ordinal),
                $"control: 扫描器必须能在组件内命中 {token}（否则 maintenance 的 0 命中不可信）");
        }

        Assert.IsTrue(
            CreateWithoutAppendPattern.IsMatch(componentText),
            "control: 扫描器必须能在组件内命中裸 CREATE（LuceneSearchEngine 的全量重建路径）");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 扫描与解析基础设施
    // ─────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> FindViolations(IEnumerable<string> assemblyNames) =>
        assemblyNames
            .Where(name => !string.IsNullOrEmpty(name))
            .Where(IsForbiddenLoadedName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

    private static bool IsForbiddenLoadedName(string assemblyName) =>
        ForbiddenAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.Ordinal))
        || ForbiddenAssemblyNames.Any(
            name => string.Equals(assemblyName, name, StringComparison.Ordinal)
                    || assemblyName.StartsWith(name + ".", StringComparison.Ordinal));

    private static bool IsForbiddenAssemblyName(string assemblyName) =>
        ForbiddenAssemblyNames.Any(name => string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>逐行找禁用名（返回 1-based 行号 + 命中 token），失败信息据此可精确定位。</summary>
    private static IReadOnlyList<(int Line, string Token)> FindLineHits(string text, IReadOnlyList<string> tokens)
    {
        var hits = new List<(int Line, string Token)>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            foreach (var token in tokens)
                if (lines[i].Contains(token, StringComparison.Ordinal))
                    hits.Add((i + 1, token));

        return hits;
    }

    private static int LineNumberOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n')
                line++;

        return line;
    }

    /// <summary>
    /// 剥掉 C# 注释（<c>//</c> / <c>/* */</c>），可选再剥掉字符串与字符字面量。
    /// <b>保留换行</b> ⇒ 行号不漂移；<b>字符串感知</b> ⇒ <c>"http://x"</c> 里的 <c>//</c> 不会被误当注释。
    /// </summary>
    private static string SanitizeSource(string source, bool removeStringLiterals)
    {
        var builder = new StringBuilder(source.Length);
        var index = 0;

        while (index < source.Length)
        {
            var current = source[index];

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    builder.Append(' ');
                    index++;
                }

                continue;
            }

            if (current == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                while (index < source.Length
                       && !(source[index] == '*' && index + 1 < source.Length && source[index + 1] == '/'))
                {
                    builder.Append(source[index] == '\n' ? '\n' : ' ');
                    index++;
                }

                if (index < source.Length)
                {
                    builder.Append("  ");
                    index += 2;
                }

                continue;
            }

            if (current == '@' && index + 1 < source.Length && source[index + 1] == '"')
            {
                var end = SkipVerbatimLiteral(source, index + 1);
                AppendLiteral(builder, source, index, end, removeStringLiterals);
                index = end;
                continue;
            }

            if (current == '"' || current == '\'')
            {
                var end = SkipRegularLiteral(source, index, current);
                AppendLiteral(builder, source, index, end, removeStringLiterals);
                index = end;
                continue;
            }

            builder.Append(current);
            index++;
        }

        return builder.ToString();
    }

    private static int SkipRegularLiteral(string source, int start, char quote)
    {
        var index = start + 1;
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (source[index] == quote)
                return index + 1;

            index++;
        }

        return source.Length;
    }

    private static int SkipVerbatimLiteral(string source, int quoteIndex)
    {
        var index = quoteIndex + 1;
        while (index < source.Length)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            if (index + 1 < source.Length && source[index + 1] == '"')
            {
                index += 2;
                continue;
            }

            return index + 1;
        }

        return source.Length;
    }

    private static void AppendLiteral(StringBuilder builder, string source, int start, int end, bool removeStringLiterals)
    {
        for (var i = start; i < end && i < source.Length; i++)
            builder.Append(removeStringLiterals && source[i] != '\n' ? ' ' : source[i]);
    }

    /// <summary>切出一个方法体：从 marker 起，到下一个 4 空格缩进的 <c>private</c> 声明为止。</summary>
    private static string SliceMethod(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var next = source.IndexOf("\n    private ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static IEnumerable<string> ProjectReferenceTargets(string csprojText) =>
        Regex.Matches(csprojText, @"<ProjectReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value);

    private static IEnumerable<string> PackageReferenceNames(string csprojText) =>
        Regex.Matches(csprojText, @"<PackageReference\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value);

    private static IEnumerable<string> InternalsVisibleToTargets(string csprojText) =>
        Regex.Matches(csprojText, @"<InternalsVisibleTo\s+Include=""([^""]+)""")
            .Select(match => match.Groups[1].Value);

    private static string StripXmlComments(string text) =>
        Regex.Replace(text, @"<!--[\s\S]*?-->", " ");

    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    /// <summary>本测试程序集的 <c>*.deps.json</c> 声明闭包（库名 + 目标节）。</summary>
    private static IReadOnlyList<string> ReadDependencyClosure()
    {
        var assemblyName = typeof(ComponentBoundaryTests).Assembly.GetName().Name!;
        var depsPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".deps.json");
        Assert.IsTrue(File.Exists(depsPath), $"expected the SDK-generated dependency manifest at {depsPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        var names = new SortedSet<string>(StringComparer.Ordinal) { assemblyName };

        if (document.RootElement.TryGetProperty("libraries", out var libraries))
            foreach (var library in libraries.EnumerateObject())
                names.Add(StripVersion(library.Name));

        if (document.RootElement.TryGetProperty("targets", out var targets))
            foreach (var target in targets.EnumerateObject())
                foreach (var library in target.Value.EnumerateObject())
                    names.Add(StripVersion(library.Name));

        return names.ToArray();
    }

    private static string StripVersion(string libraryKey)
    {
        var separator = libraryKey.IndexOf('/');
        return separator < 0 ? libraryKey : libraryKey[..separator];
    }

    private static string ComponentDirectory() => Path.Combine(RepositoryRoot(), ComponentRelativeDirectory);

    private static string ReadComponentSource(string relativePath) =>
        File.ReadAllText(Path.Combine(ComponentDirectory(), relativePath));

    /// <summary>组件的全部 <c>.cs</c>（相对组件根的路径，序数排序），排除 <c>bin/</c> 与 <c>obj/</c> 生成物。</summary>
    private static string[] EnumerateComponentSources() =>
        EnumerateComponentSourcePaths()
            .Select(path => Path.GetRelativePath(ComponentDirectory(), path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static string[] EnumerateComponentSourcePaths() => EnumerateSourcePaths(ComponentDirectory());

    private static string[] EnumerateMaintenanceSourcePaths() =>
        EnumerateSourcePaths(Path.Combine(ComponentDirectory(), MaintenanceRelativeDirectory));

    private static string[] EnumerateSourcePaths(string root)
    {
        Assert.IsTrue(Directory.Exists(root), $"expected the scan root at {root}");

        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsBuildOutput(string fullPath) =>
        fullPath.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)
        || fullPath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && directory is not null; i++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PuddingAgentNetwork.slnx")))
                return directory.FullName;
        }

        Assert.Fail("cannot locate the repository root above " + AppContext.BaseDirectory);
        return string.Empty;
    }
}
