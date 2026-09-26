using PuddingFullTextIndex;
using PuddingFullTextIndex.Contracts;
using PuddingFullTextIndex.Infrastructure;
using PuddingFullTextIndex.Infrastructure.Maintenance;
using PuddingFullTextIndex.Infrastructure.Search;
using PuddingFullTextIndex.Infrastructure.Supply;
using Directory = System.IO.Directory;

namespace PuddingFullTextIndexTests;

/// <summary>
/// S3e：**跨路径互斥的可证性** —— 同一个语料根下，维护路径（直写 live）与供给路径（staging 整目录替换）
/// 取得的是否是**同一个跨进程租约键**（⇒ 二者互斥，不可能并发写同一索引目录）。
/// <para>
/// 依据（唯一权威）：任务书 <c>temp/s3e-cross-path-mutex-task.md</c>（I-MUTEX、§2 实测、M1~M5、M-X1/M-X2、红线）。
/// </para>
/// <para>
/// <b>§2 先从磁盘取出的承载面（实测，不是照抄任务书）</b>：
/// <list type="number">
/// <item><description><c>FullTextChangeSet.ScopeKey</c> 是**不透明字符串**：记录本身<b>不做</b>任何规范化，
/// 语义定义为「与既有供给链同口径」，见 <c>Contracts/FullTextChangeSet.cs:78</c>。</description></item>
/// <item><description>维护循环构造变更集时**逐字透传** scope 描述符：
/// <c>LuceneFullTextIndexMaintenance.cs:1021</c> 用 <c>scope.ScopeKey</c>，而 <c>ScopeRuntime.ScopeKey</c>
/// 来自**调用方**提供的 <c>FullTextMaintenanceScope</c>（<c>Contracts/IFullTextIndexMaintenance.cs:62</c>：
/// 「调用方负责把配置里的原始 scope 路径解析成 RootPath / ScopeKey」）。</description></item>
/// <item><description>因此维护侧的键推导真源在**配置解析层**，组件内可见的推导入口是
/// <c>FullTextChangeCoalescer.NormalizeComparisonKey</c>（<c>MaintenanceOptions.cs:416</c> 的 scope 解析、
/// <c>LuceneFullTextIndexMaintenance.cs:1539</c> 的 <c>FindScopeLocked</c>、
/// <c>LuceneFullTextIndexMaintenanceEngine.cs:917</c> 的 <c>ProbeIntegrityAsync</c> 都用它）。</description></item>
/// <item><description>供给侧真源在 <c>SupplyScopeNormalizer.Normalize</c>（<c>SupplyScopeNormalizer.cs:92</c>）
/// → <c>ToScopeKey</c>（同文件 :121）；租约文件 = <c>&lt;IndexRoot&gt;/.supply-leases/&lt;sha256(scopeKey)&gt;.json</c>
/// （<c>FileSupplyLease.cs:79</c>）。</description></item>
/// </list>
/// ⇒ 断言对象按实测调整：<b>维护侧入口 = <c>FullTextChangeCoalescer.NormalizeComparisonKey</c></b>
/// （而非任务书设想的某个「维护 scope 描述符工厂」——组件内并不存在这样的工厂），供给侧入口 = <c>SupplyScopeNormalizer</c>。
/// </para>
/// <para>
/// <b>★ 本片结论（实测，详见 <c>temp/s3e-report.md</c>）</b>：
/// <list type="number">
/// <item><description><b>普通目录：已证实</b> —— 两条路径经同一 <c>ToScopeKey</c> 口径得到逐字符相同的键
/// ⇒ 同一个租约文件 ⇒ 行为级互斥（M1/M2/M4a/M4b）。</description></item>
/// <item><description><b>盘根形态：已证伪</b> —— 供给侧保盘根（<c>"c:\"</c>），维护侧 <c>NormalizeComparisonKey</c>
/// 去尾分隔符后<b>不</b>补回（<c>"c:"</c>）⇒ 不同租约文件 ⇒ 不互斥（M2 的 <c>drive-root-invariant-falsified</c>）。</description></item>
/// <item><description><b>同进程默认身份：不互斥</b> —— 租约按 <c>OwnerId</c> 判「自己人」而重入，
/// 而供给协调器与维护内核的默认 owner 相同 ⇒ 同进程内两条路径可并发（M4c）。</description></item>
/// </list>
/// </para>
/// <para>
/// 红线：语料与索引根一律落在 <c>%TEMP%</c>（<see cref="Initialize"/> 有硬断言、绝不以 <c>D:\data</c> 开头）；
/// 本文件零后台线程、零 git 操作；临时索引根全是独立的 <c>%TEMP%</c> 目录。
/// </para>
/// </summary>
[TestClass]
public sealed class CrossPathMutexTests
{
    private const long DefaultMaxIndexBytes = 1_073_741_824L;
    private const int DefaultMaxPaths = 512;

    /// <summary>可区分的维护侧 owner（M4 用它制造「另一个持有者」；默认身份在同一进程内会被判为「自己人」而重入）。</summary>
    private const string MaintenanceLeaseOwnerId = "s3e-maintenance-path#1111";

    /// <summary>可区分的供给侧 owner。</summary>
    private const string SupplyLeaseOwnerId = "s3e-supply-path#2222";

    public TestContext TestContext { get; set; } = null!;

    private string _root = null!;
    private string _indexRoot = null!;
    private string _corpus = null!;
    private string _otherCorpus = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "pudding-fts-s3e-" + Guid.NewGuid().ToString("N"));

        // 硬断言（红线 R3）：语料与索引只允许落在 %TEMP% 下，绝不碰生产索引根 D:\data\fulltext-index
        Assert.IsTrue(
            _root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase),
            $"测试根必须在 %TEMP% 下，实际 {_root}");
        Assert.IsFalse(
            _root.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"测试根绝不允许落在生产数据根下，实际 {_root}");

        _indexRoot = Path.Combine(_root, "index");
        _corpus = Path.Combine(_root, "corpus");
        _otherCorpus = Path.Combine(_root, "corpus-other");
        Directory.CreateDirectory(_corpus);
        Directory.CreateDirectory(_otherCorpus);

        Assert.IsFalse(
            _indexRoot.StartsWith(@"D:\data", StringComparison.OrdinalIgnoreCase),
            $"临时索引根绝不允许落在生产数据根下，实际 {_indexRoot}");
    }

    /// <summary>
    /// 失败路径（断言失败 / 变异取红）也必须把 <c>%TEMP%</c> 清干净；清不掉就响亮报错（转红），绝不静默。
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        var remaining = TryDeleteRoot();

        for (var attempt = 0; attempt < 5 && remaining is not null; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(50);
            remaining = TryDeleteRoot();
        }

        Assert.IsNull(remaining, $"失败路径也必须清理 %TEMP%（残留目录 {_root}：{remaining}）");
    }

    private string? TryDeleteRoot()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);

            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    // ── M1：键一致性（等价描述符 ⇒ 同键 + 同租约文件）────────────────────

    [TestMethod]
    public void M1_BothPaths_Derive_The_Same_ScopeKey_And_The_Same_LeaseFile()
    {
        var options = Options();

        var maintenanceKey = MaintenanceSideKey(_corpus);
        var supplyKey = SupplySideKey(_corpus);

        Assert.AreEqual(
            supplyKey,
            maintenanceKey,
            "I-MUTEX：同一语料根，维护路径与供给路径推出的 scopeKey 必须逐字符相同");

        var maintenanceLeaseFile = FileSupplyLease.ResolveLeaseFilePath(options, maintenanceKey);
        var supplyLeaseFile = FileSupplyLease.ResolveLeaseFilePath(options, supplyKey);

        Assert.AreEqual(
            maintenanceLeaseFile,
            supplyLeaseFile,
            "键相同 ⇒ 由 FileSupplyLease 推出的租约文件必须是同一个（跨进程互斥的承载面）");

        Assert.AreEqual(
            Path.Combine(_indexRoot, ".supply-leases"),
            Path.GetDirectoryName(maintenanceLeaseFile),
            "租约必须落在索引根下的 .supply-leases（与 IndexRootWriteGate 的进程内互斥不是同一层）");

        // ★ 反混淆（任务书 §2.5）：索引目录名的哈希输入与 scope 键是**两套口径**，不得当成同一物。
        var namingHashInput = FullTextIndexPaths.NormalizeCorpusRoot(_corpus);
        Assert.AreNotEqual(
            namingHashInput,
            maintenanceKey,
            "命名哈希输入（大写、不保盘根）与 scope 键（不变文化小写、保盘根）必须是两套不同口径");

        var indexDirectory = FullTextIndexPaths.ResolveIndexDirectory(_indexRoot, _corpus);
        Assert.AreNotEqual(
            Path.GetFileName(indexDirectory),
            Path.GetFileName(maintenanceLeaseFile),
            "索引目录名（sha256(命名哈希输入)）与租约文件名（sha256(scopeKey)）不得被混为一谈");

        TestContext.WriteLine(
            $"M1 maintenanceKey={maintenanceKey} supplyKey={supplyKey} leaseFile={maintenanceLeaseFile}");
    }

    // ── M2：边界矩阵（每条独立判定；失败点名是哪条输入）───────────────────

    [TestMethod]
    [DataRow("trailing-backslash")]
    [DataRow("trailing-forward-slash")]
    [DataRow("trailing-multiple-separators")]
    [DataRow("upper-cased-root")]
    [DataRow("lower-cased-root")]
    [DataRow("drive-letter-case")]
    [DataRow("duplicate-separators")]
    [DataRow("dot-segment")]
    [DataRow("dotdot-segment")]
    [DataRow("already-normalized-idempotent")]
    [DataRow("drive-root-invariant-falsified")]
    [DataRow("relative-vs-absolute")]
    [DataRow("missing-absolute")]
    public void M2_Boundary_Matrix_Each_Input_Is_Judged_On_Its_Own(string caseName)
    {
        var options = Options();
        var raw = RawFor(caseName);

        var supply = SupplyScopeNormalizer.Normalize(new[] { raw });
        var maintenanceKey = TryMaintenanceSideKey(raw, out var maintenanceError);

        TestContext.WriteLine(
            $"M2:{caseName} raw={raw} supplyAccepted={supply.Accepted.Count} "
            + $"supplyRejected={supply.Rejections.Count} maintenanceKey={maintenanceKey ?? "(throws)"} err={maintenanceError}");

        switch (caseName)
        {
            // ① 合法地在两侧被**不同**处理：相对路径一侧拒绝、一侧按 CWD 解析接受（任务书明确要求如实判定，不算缺陷）
            case "relative-vs-absolute":
                EnsureSupplyRejected(supply, SupplyRejectionReason.Relative, caseName);
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(maintenanceKey),
                    $"{caseName}：维护侧（配置解析层）按 WorkspaceRoot/CWD 解析相对 scope，必须接受；"
                    + "这不是键不一致，而是**合法的不对称**：供给侧根本不接受相对路径 ⇒ 不可能与维护侧争同一目录。");
                break;

            // ①' 同上：绝对但不存在 ⇒ 供给侧拒绝（NotFound），维护侧只算键、不判存在性
            case "missing-absolute":
                EnsureSupplyRejected(supply, SupplyRejectionReason.NotFound, caseName);
                Assert.IsFalse(string.IsNullOrWhiteSpace(maintenanceKey), $"{caseName}：维护侧只做键推导，不判存在性");
                break;

            default:
                Assert.AreEqual(0, supply.Rejections.Count, $"{caseName}：供给侧必须接受（{DescribeRejections(supply)}）");
                Assert.AreEqual(1, supply.Accepted.Count, $"{caseName}：供给侧必须恰好接受 1 个 scope");
                Assert.IsNull(maintenanceError, $"{caseName}：维护侧推导不得抛异常（{maintenanceError}）");

                if (caseName == "drive-root-invariant-falsified")
                {
                    // ★ 本片最重要的输出：盘根形态下两侧**不同键** ⇒ I-MUTEX 在此输入上**被证伪**（实测，非推理）。
                    // 机理：供给侧保盘根（"c:\"）；维护侧 NormalizeComparisonKey 先 TrimEnd 分隔符、
                    // 只在「裁剪后为空」时才补回，故盘根变成 "c:" ⇒ 两条路径落在**不同租约文件**上
                    // ⇒ 并发写同一索引目录不可能被租约挡住。
                    // 红线 R5：不为让断言变绿而放宽、也不允许本片顺手改生产代码 ⇒ 这里把**实测值**钉住，
                    // 证伪结论与 RED 原始输出见 temp/s3e-report.md 的 BLOCKERS。
                    var driveRoot = Path.GetPathRoot(_corpus)!;
                    var rootPreservingKey = driveRoot.ToLowerInvariant();
                    var rootStrippingKey = driveRoot.TrimEnd('\\', '/').ToLowerInvariant();

                    Assert.AreEqual(rootPreservingKey, supplyKeyOf(supply), "实测：供给侧（保盘根）对盘根给出的键");
                    Assert.AreEqual(
                        rootStrippingKey,
                        maintenanceKey,
                        "实测：维护侧对盘根**去掉**尾分隔符且不补回（键比供给侧少一个尾分隔符）");
                    Assert.AreNotEqual(
                        rootPreservingKey,
                        maintenanceKey,
                        "I-MUTEX 要求两键逐字符相同；实测不同 ⇒ 该输入上不变量被证伪");
                    Assert.AreNotEqual(
                        FileSupplyLease.ResolveLeaseFilePath(options, supplyKeyOf(supply)),
                        FileSupplyLease.ResolveLeaseFilePath(options, maintenanceKey!),
                        "★ 已证伪：盘根语料根下两条路径推出**不同的租约文件** ⇒ 二者不互斥（I-MUTEX 在此输入上不成立）");
                    break;
                }

                Assert.IsTrue(
                    Directory.Exists(Path.GetFullPath(raw)),
                    $"{caseName}：该输入必须指向真实存在的语料根（否则下面的相等断言会因前置条件失败而失去意义）");
                Assert.AreEqual(
                    supplyKeyOf(supply),
                    maintenanceKey,
                    $"{caseName}：两侧推出的 scopeKey 必须逐字符相同（raw={raw}）");
                Assert.AreEqual(
                    FileSupplyLease.ResolveLeaseFilePath(options, supplyKeyOf(supply)),
                    FileSupplyLease.ResolveLeaseFilePath(options, maintenanceKey!),
                    $"{caseName}：两侧推出的租约文件必须相同（raw={raw}）");
                break;
        }
    }

    /// <summary>
    /// M2 的**规范化语义**补充（跨输入折叠 + 幂等）：同一语料根的不同写法必须折叠到同一个键。
    /// <para>
    /// 为什么必要：只看「同一个 raw 在两侧是否同键」无法发现「两侧都退化成恒等」——
    /// 恒等实现下同一个目录的大小写变体会得到两个不同的键，而 M2 的逐输入用例仍然全绿。
    /// 本用例是 **M-X2（把规范化改成恒等）的取红靶点**。
    /// </para>
    /// </summary>
    [TestMethod]
    public void M2b_Normalization_Folds_Equivalent_Spellings_To_The_Same_Key()
    {
        var canonical = SupplySideKey(_corpus);
        var canonicalMaintenance = MaintenanceSideKey(_corpus);

        // ① 大小写折叠（规范键必须是不变文化小写）
        Assert.AreEqual(
            canonical,
            SupplySideKey(_corpus.ToUpperInvariant()),
            "供给侧：同一语料根的大小写变体必须折叠到同一个键");
        Assert.AreEqual(
            canonicalMaintenance,
            MaintenanceSideKey(_corpus.ToUpperInvariant()),
            "维护侧：同一语料根的大小写变体必须折叠到同一个键");
        Assert.AreEqual(canonical, canonical.ToLowerInvariant(), "规范键必须已经是不变文化小写");

        // ② 尾分隔符折叠（两侧）
        Assert.AreEqual(canonical, SupplySideKey(_corpus + "\\"), "供给侧：尾分隔符必须折叠");
        Assert.AreEqual(canonicalMaintenance, MaintenanceSideKey(_corpus + "\\"), "维护侧：尾分隔符必须折叠");

        // ③ 分隔符统一（正斜杠写法）
        Assert.AreEqual(canonical, SupplySideKey(_corpus.Replace('\\', '/')), "供给侧：/ 与 \\ 必须折叠到同一个键");
        Assert.AreEqual(
            canonicalMaintenance,
            MaintenanceSideKey(_corpus.Replace('\\', '/')),
            "维护侧：/ 与 \\ 必须折叠到同一个键");

        // ④ 幂等：键再喂回去必须得到同一个键
        Assert.AreEqual(canonical, SupplySideKey(canonical), "供给侧：规范化必须幂等");
        Assert.AreEqual(canonicalMaintenance, MaintenanceSideKey(canonicalMaintenance), "维护侧：规范化必须幂等");

        // ⑤ 防空转：键必须真的被规范化过，不得原样回传入参
        Assert.AreNotEqual(_corpus, canonical, "键不得原样回传入参");

        TestContext.WriteLine($"M2b canonical={canonical}");
    }

    private string RawFor(string caseName) => caseName switch
    {
        "trailing-backslash" => _corpus + "\\",
        "trailing-forward-slash" => _corpus + "/",
        "trailing-multiple-separators" => _corpus + "\\/",
        "upper-cased-root" => _corpus.ToUpperInvariant(),
        "lower-cased-root" => _corpus.ToLowerInvariant(),
        "drive-letter-case" => char.ToLowerInvariant(_corpus[0]) + _corpus[1..],
        "duplicate-separators" => _corpus.Replace("\\", "\\\\", StringComparison.Ordinal),
        "dot-segment" => Path.Combine(_corpus, "."),
        "dotdot-segment" => Path.Combine(CreateSubDirectory(), ".."),
        "already-normalized-idempotent" => SupplySideKey(_corpus),
        "drive-root-invariant-falsified" => Path.GetPathRoot(_corpus)!,
        "relative-vs-absolute" => @"Source\PuddingFullTextIndex",
        "missing-absolute" => _corpus + "-does-not-exist",
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "未知的边界矩阵用例"),
    };

    private string CreateSubDirectory()
    {
        var sub = Path.Combine(_corpus, "sub");
        Directory.CreateDirectory(sub);
        return sub;
    }

    // ── M3：正对照（防空转）──────────────────────────────────────────────

    [TestMethod]
    public void M3_Positive_Control_Two_Distinct_CorpusRoots_Yield_Distinct_Keys_And_Lease_Files()
    {
        var options = Options();

        var supplyA = SupplySideKey(_corpus);
        var supplyB = SupplySideKey(_otherCorpus);
        var maintenanceA = MaintenanceSideKey(_corpus);
        var maintenanceB = MaintenanceSideKey(_otherCorpus);

        Assert.AreNotEqual(supplyA, supplyB, "两个明确不同的语料根不得退化成同一个键（防空转）");
        Assert.AreNotEqual(maintenanceA, maintenanceB, "维护侧同样不得退化成常量键（防空转）");

        Assert.AreNotEqual(
            FileSupplyLease.ResolveLeaseFilePath(options, supplyA),
            FileSupplyLease.ResolveLeaseFilePath(options, supplyB),
            "不同语料根 ⇒ 不同租约文件（防空转）");

        Assert.IsFalse(string.IsNullOrWhiteSpace(supplyA), "键不得为空");
        Assert.IsFalse(string.IsNullOrWhiteSpace(maintenanceA), "键不得为空");
        Assert.AreNotEqual(_corpus, supplyA, "键必须是规范化产物（小写），不得原样回传入参");

        TestContext.WriteLine($"M3 A={supplyA} B={supplyB}");
    }

    // ── M4：互斥的行为级证明（真实 FileSupplyLease + 两个可区分 Owner）──────

    [TestMethod]
    public async Task M4a_Real_Lease_Two_Distinguishable_Owners_Maintenance_Holds_Then_Supply_Is_Refused()
    {
        var options = Options();
        var lease = new FileSupplyLease(options);

        var maintenanceKey = MaintenanceSideKey(_corpus);
        var supplyKey = SupplySideKey(_corpus);
        var maintenanceOwner = new SupplyLeaseOwner(MaintenanceLeaseOwnerId, 1111, "MACHINE-MAINT");
        var supplyOwner = new SupplyLeaseOwner(SupplyLeaseOwnerId, 2222, "MACHINE-SUPPLY");

        var acquired = await lease.TryAcquireAsync(maintenanceKey, maintenanceOwner, "maintenance-batch-01");
        Assert.IsTrue(acquired.Acquired, acquired.Message);

        // ★ 行为级证据：维护侧的键与供给侧的键指向**同一个租约文件** ⇒ 第二条路径必然失败
        var refused = await lease.TryAcquireAsync(supplyKey, supplyOwner, "supply-job-01");
        Assert.IsFalse(
            refused.Acquired,
            $"维护侧持有（键={maintenanceKey}）时，供给侧按自己的键（{supplyKey}）必须拿不到；实际取得了 ⇒ 两条路径未互斥");
        Assert.IsNotNull(refused.Holder, "拒绝必须报出当前持有者");
        Assert.AreEqual(MaintenanceLeaseOwnerId, refused.Holder!.OwnerId, "拒绝必须点名真正的持有者");

        // 正对照：维护侧释放后，供给侧必须能干净取得（证明上面的 false 不是「恒 false」）
        Assert.IsTrue(await lease.ReleaseAsync(maintenanceKey, maintenanceOwner.OwnerId), "持有者必须能释放自己的租约");
        var afterRelease = await lease.TryAcquireAsync(supplyKey, supplyOwner, "supply-job-02");
        Assert.IsTrue(afterRelease.Acquired, afterRelease.Message);
        Assert.IsNull(afterRelease.Lease!.TakeoverReason, "正对照必须是干净取得，而不是托管接管");

        TestContext.WriteLine($"M4a maintenanceKey={maintenanceKey} supplyKey={supplyKey}");
    }

    [TestMethod]
    public async Task M4b_Behavior_Supply_Path_Holding_The_Lease_Makes_The_Maintenance_Path_Busy_Without_Writing()
    {
        var options = Options();
        var corpusFile = Path.Combine(_corpus, "a.txt");
        File.WriteAllText(corpusFile, "s3e cross path mutex marker zzs3emutexmark");

        // 先做一次全量构建，让 live 索引目录存在（维护侧的守卫 B 要求 live 索引已存在）
        using (var buildSearch = new LuceneSearchEngine(options))
        {
            var build = await buildSearch.BuildIndexAsync(_corpus);
            Assert.IsTrue(build.Success, $"初始全量构建必须成功：{build.Error}");
        }

        // 供给路径：真实 FileSupplyLease + 可区分 owner，用一个「挂住」的 builder 让租约在断言期间持续被持有
        var builderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new StubSupplyBuilder(async (_, ct) =>
        {
            builderStarted.TrySetResult();
            await releaseBuilder.Task.WaitAsync(ct).ConfigureAwait(false);
            return SupplyTestHelpers.Success();
        });

        var coordinator = new FullTextIndexSupplyCoordinator(
            new StubSupplyInventory(),
            builder,
            new FileSupplyLease(options),
            new SupplyCoordinatorOptions { OwnerId = SupplyLeaseOwnerId, MinRebuildInterval = TimeSpan.Zero });

        var outcome = await coordinator.BuildAsync(new SupplyScopeRequest(_corpus));
        var scopeOutcome = SupplyTestHelpers.SingleScopeOutcome(outcome);
        Assert.AreEqual(SupplyOutcome.Started, scopeOutcome.Outcome, scopeOutcome.Reason);
        await builderStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // 维护路径：真实内核 + 真实文件租约 + 自己的键（owner = 当前进程默认身份，与供给 owner 可区分）
        var invalidations = new CountingInvalidation();
        using var maintenanceSearch = new LuceneSearchEngine(options);
        var engine = new LuceneFullTextIndexMaintenanceEngine(
            maintenanceSearch,
            options,
            new FileSupplyLease(options),
            TimeSpan.FromMilliseconds(200),
            invalidations);

        var changeSet = new FullTextChangeSet(
            "01K-S3E-BATCH-01",
            _corpus,
            MaintenanceSideKey(_corpus),
            new[] { Upsert(corpusFile) },
            DateTimeOffset.UtcNow,
            PreviousWatermarkUtc: null,
            RequiresCheckpointAdvance: true);

        var result = await engine.ApplyChangesAsync(changeSet, GenerousBudget(options));

        Assert.AreEqual(
            FullTextMutationState.Busy,
            result.State,
            $"供给侧持租约时，维护路径必须 Busy（不写、不提交），实际 {result.State}：{result.Message}");
        StringAssert.Contains(result.Message, "互斥失败", "Busy 必须指名是跨进程租约互斥");
        Assert.IsNull(result.CommitMilliseconds, "Busy ⇒ 不得提交");
        Assert.IsFalse(result.CheckpointAdvanced, "Busy ⇒ 不得推进 checkpoint");
        Assert.AreEqual(0, invalidations.Count, "Busy ⇒ 0 次 reader 失效");

        // 收尾：让供给 job 正常结束，释放租约（不留下悬挂持有者）
        releaseBuilder.TrySetResult();
        await SupplyTestHelpers.AwaitJobAsync(coordinator, scopeOutcome.JobId!).ConfigureAwait(false);

        // 正对照：租约释放后，同一变更集必须能真正应用（证明 Busy 不是恒真）
        var replay = await engine.ApplyChangesAsync(changeSet, GenerousBudget(options));
        Assert.AreEqual(
            FullTextMutationState.Applied,
            replay.State,
            $"租约释放后同一变更集必须成功（正对照）：{replay.Message}");

        TestContext.WriteLine($"M4b supplyJob={scopeOutcome.JobId} busyMessage={result.Message}");
    }

    /// <summary>
    /// M4 的**边界**（如实钉住，不是把缺陷当正确）：文件租约以 <c>OwnerId</c> 判「自己人」，
    /// 而供给协调器与维护内核的**默认 owner 都是** <see cref="SupplyLeaseOwner.ForCurrentProcess"/> ⇒
    /// 同一进程内，第二条路径会**重入成功**而不是被拒。
    /// <para>
    /// 结论：I-MUTEX 只在「owner 可区分」（跨进程 / 显式不同 OwnerId）时成立；
    /// 同进程默认身份下两条路径不互斥 —— 且两条路径的**进程内**闸门也不是同一把
    /// （供给协调器的 per-scope gate vs <c>LuceneSearchEngine.GetScopeGate</c> + <c>IndexRootWriteGate</c>）。
    /// 该结论进 <c>temp/s3e-report.md</c> 的 RISKS/BLOCKERS。
    /// </para>
    /// </summary>
    [TestMethod]
    public async Task M4c_Boundary_Same_OwnerId_Reentrancy_Means_Two_Paths_In_One_Process_Are_Not_Mutually_Exclusive()
    {
        var options = Options();
        var lease = new FileSupplyLease(options);
        var sameOwner = SupplyLeaseOwner.ForCurrentProcess();

        var maintenanceKey = MaintenanceSideKey(_corpus);
        var supplyKey = SupplySideKey(_corpus);

        Assert.AreEqual(
            FileSupplyLease.ResolveLeaseFilePath(options, maintenanceKey),
            FileSupplyLease.ResolveLeaseFilePath(options, supplyKey),
            "前置：两条路径确实指向同一个租约文件（否则本用例证明的不是重入）");

        Assert.AreEqual(
            sameOwner.OwnerId,
            new SupplyCoordinatorOptions().OwnerId,
            "成因：供给协调器的默认 owner 与维护内核硬编码的 owner 都是 ForCurrentProcess()（同一进程 ⇒ 同一 OwnerId）");

        var first = await lease.TryAcquireAsync(maintenanceKey, sameOwner, "maintenance-batch-01");
        Assert.IsTrue(first.Acquired, first.Message);

        var second = await lease.TryAcquireAsync(supplyKey, sameOwner, "supply-job-01");
        Assert.IsTrue(
            second.Acquired,
            "实测：同一 OwnerId 被视为可重入 ⇒ 同进程内维护与供给两条路径**并不互斥**（跨进程才互斥）");

        var holder = await lease.DescribeHolderAsync(maintenanceKey);
        Assert.IsNotNull(holder, "重入后租约文件必须仍可读");
        Assert.AreEqual("supply-job-01", holder!.JobId, "重入覆盖了 job 归属 ⇒ 原持有者的批次与实际写入者不再可区分");

        TestContext.WriteLine($"M4c sameOwner={sameOwner.OwnerId} secondAcquiredJob={holder.JobId}");
    }

    // ── M5：IndexRootWriteGate 的层级声明（文本级，带正/负对照）────────────

    [TestMethod]
    public void M5_IndexRootWriteGate_Declares_InProcess_Mutex_Not_A_Substitute_For_The_CrossProcess_Lease()
    {
        var gatePath = Path.Combine(
            RepositoryRoot(),
            "Source",
            "PuddingFullTextIndex",
            "Infrastructure",
            "Maintenance",
            "IndexRootWriteGate.cs");

        Assert.IsTrue(File.Exists(gatePath), $"仪器必须能读到真实源文件：{gatePath}");
        var text = File.ReadAllText(gatePath);

        // 正对照（同一仪器、同一文件）：已知 token 必须命中 ⇒ 证明仪器不是「恒 false」
        StringAssert.Contains(text, "IndexRootWriteGate", "正对照：同一仪器必须命中已知 token");
        // 负对照：不可能存在的 token 必须不命中 ⇒ 证明仪器不是「恒 true」
        Assert.IsFalse(
            text.Contains("S3E_ABSENT_TOKEN_SENTINEL", StringComparison.Ordinal),
            "负对照：不存在的 token 不得命中");

        // 待证声明（任务书 M5）
        StringAssert.Contains(text, "进程内", "必须声明这是**进程内**互斥");
        StringAssert.Contains(text, "不替代跨进程租约", "必须声明它**不替代**跨进程租约");

        // 同仪器的**跨文件**对照：另一个真实文件不得含该声明 ⇒ 证明命中来自内容而非仪器偏差
        var otherPath = Path.Combine(
            RepositoryRoot(),
            "Source",
            "PuddingFullTextIndex",
            "Infrastructure",
            "Supply",
            "SupplyScopeNormalizer.cs");
        Assert.IsTrue(File.Exists(otherPath), $"仪器必须能读到对照文件：{otherPath}");
        Assert.IsFalse(
            File.ReadAllText(otherPath).Contains("不替代跨进程租约", StringComparison.Ordinal),
            "对照：同一仪器在另一个真实文件上不得命中该声明");

        TestContext.WriteLine($"M5 gatePath={gatePath} declarationLine={LineOf(text, "不替代跨进程租约")}");
    }

    // ── 帮助 ──────────────────────────────────────────────────────────────

    private FullTextIndexOptions Options() => new() { IndexRootDirectory = _indexRoot };

    /// <summary>
    /// 维护侧入口（实测的承载面）：组件内维护链的键推导真源。
    /// 用于 <c>MaintenanceOptions.Validate</c> 的 scope 解析、<c>FindScopeLocked</c>、<c>ProbeIntegrityAsync</c>，
    /// 也是组件外调用方唯一可用的公开键推导 helper（<c>SupplyScopeNormalizer</c> 是 internal）。
    /// </summary>
    private static string MaintenanceSideKey(string scopeRoot) =>
        FullTextChangeCoalescer.NormalizeComparisonKey(scopeRoot);

    private static string? TryMaintenanceSideKey(string scopeRoot, out string? error)
    {
        try
        {
            error = null;
            return MaintenanceSideKey(scopeRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    /// <summary>供给侧入口（实测的承载面）：协调器内部的规范化（<c>SupplyScopeNormalizer.Normalize</c>）。</summary>
    private static string SupplySideKey(string scopeRoot)
    {
        var result = SupplyScopeNormalizer.Normalize(new[] { scopeRoot });
        Assert.AreEqual(
            1,
            result.Accepted.Count,
            $"供给侧必须接受 scope「{scopeRoot}」（拒绝记录：{DescribeRejections(result)}）");
        return result.Accepted[0].ScopeKey;
    }

    private static string supplyKeyOf(SupplyScopeNormalizer.NormalizationResult result)
    {
        Assert.AreEqual(1, result.Accepted.Count, $"供给侧必须恰好接受 1 个 scope（{DescribeRejections(result)}）");
        return result.Accepted[0].ScopeKey;
    }

    private static string DescribeRejections(SupplyScopeNormalizer.NormalizationResult result) =>
        result.Rejections.Count == 0
            ? "(none)"
            : string.Join(" | ", result.Rejections.Select(r => $"{r.Reason}:{r.Value}:{r.Message}"));

    private void EnsureSupplyRejected(
        SupplyScopeNormalizer.NormalizationResult result,
        SupplyRejectionReason expected,
        string caseName)
    {
        Assert.AreEqual(0, result.Accepted.Count, $"{caseName}：供给侧必须拒绝该输入");
        Assert.AreEqual(1, result.Rejections.Count, $"{caseName}：必须恰好一条拒绝记录（{DescribeRejections(result)}）");
        Assert.AreEqual(expected, result.Rejections[0].Reason, $"{caseName}：拒绝原因不符（{result.Rejections[0].Message}）");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Rejections[0].Message), $"{caseName}：拒绝必须带可读消息");
    }

    private FullTextMutationBudget GenerousBudget(FullTextIndexOptions options) =>
        new(
            MaxIndexBytes: DefaultMaxIndexBytes,
            LiveIndexBytes: SupplyIndexDirectoryLayout.MeasureLiveIndexBytes(options.IndexRootDirectory),
            MaxPaths: DefaultMaxPaths);

    private static FullTextFileChange Upsert(string fullPath) =>
        new(fullPath, FullTextChangeKind.Upsert, LastWriteUtc: null, Length: null, Sources: FullTextChangeSource.Watcher);

    private static int LineOf(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.Ordinal);
        if (index < 0)
            return -1;

        var line = 1;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n')
                line++;

        return line;
    }

    /// <summary>从测试程序集向上找仓库根（判据：<c>PuddingAgentNetwork.slnx</c>）。</summary>
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

    /// <summary>只计数、**不转发**的失效接缝（M4b 断言「Busy ⇒ 0 次失效」）。</summary>
    private sealed class CountingInvalidation : IScopeReaderInvalidation
    {
        private int _count;

        internal int Count => Volatile.Read(ref _count);

        public void InvalidateScope(string scopeRoot) => Interlocked.Increment(ref _count);
    }
}
